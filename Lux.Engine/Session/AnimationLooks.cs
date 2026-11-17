using System.Globalization;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Animate;
using Lux.Engine.Pipeline.DepthRefine;
using Lux.Engine.Pipeline.Dof;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Session;

// The effects that are made from one render of the picture (and its depth) and then drawn frame by frame: the zoom-through, the fog, the
// pan-and-zoom, the slice reveal and the tilt-shift sweep. Each one is a Look: it resolves the photo's defaults and checks the photo when
// it is created, and draws any frame on request.
public sealed partial class AnimationRenderer
{
    /// <summary>One of the effects of this file, resolved against a capture.</summary>
    abstract class Look
    {
        public (int W, int H) Size;
        public int Level;
        public readonly List<string> Notes = new();
        public abstract string Label(int k, int n);
        public abstract Rgba Render(int k, int n, CancellationToken cancel);
    }

    Look? _look;

    static bool IsLook(AnimationEffect e) =>
        e is AnimationEffect.ZoomThrough or AnimationEffect.Fog or AnimationEffect.PanZoom or AnimationEffect.SliceReveal or AnimationEffect.TiltShift;

    static AnimationEffectCheck CheckZoomThrough(LriFile lri)
    {
        int refGroup = Wigglegram.Group((int)lri.Modules[lri.ReferenceModule].Module.Id);
        bool tele = lri.Modules.Any(m => Wigglegram.Group((int)m.Value.Module.Id) > refGroup);
        return tele
            ? new AnimationEffectCheck(AnimationEffect.ZoomThrough, true, null, "Made from the full-size picture. Works best in daylight on subjects farther than a few meters.")
            : new AnimationEffectCheck(AnimationEffect.ZoomThrough, false, "This photo has no pictures from longer lenses, so there is no extra detail to zoom onto.", null);
    }

    static async Task<AnimationRenderer> CreateLookAsync(CaptureSession s, AnimationSpec spec, Action<string>? log, CancellationToken cancel)
    {
        var check = Check(s).First(c => c.Effect == spec.Effect);
        if (!check.Available) throw new ExportRequestException(check.Reason ?? "this capture cannot make this animation");
        int n = Math.Clamp(spec.Frames, 2, 240);
        var look = await Task.Run<Look>(() =>
        {
            s.DepthState();
            return spec.Effect switch
            {
                AnimationEffect.ZoomThrough => new ZoomLook(s, spec, n, log, cancel),
                AnimationEffect.Fog => new FogFrames(s, spec, cancel),
                AnimationEffect.PanZoom => new PanLook(s, spec, log, cancel),
                AnimationEffect.SliceReveal => new SliceFrames(s, spec, cancel),
                _ => new TiltLook(s, spec, cancel),
            };
        }, cancel).ConfigureAwait(false);
        var frames = new List<AnimationFrameInfo>(n);
        for (int k = 0; k < n; k++) frames.Add(new AnimationFrameInfo(k, look.Label(k, n)));
        var order = Enumerable.Range(0, n).ToList();
        int ms = Math.Max(10, spec.EffectiveFrameMs);
        if (spec.EffectivePingPong && n >= 3) for (int i = n - 2; i >= 1; i--) order.Add(i);
        else
        {
            // the last frame stays: it is played again for the hold time (every container shows that, video included)
            int hold = Math.Clamp(spec.HoldMs ?? AnimationSpec.DefaultHoldMs(spec.Effect), 0, 10000) / ms;
            for (int i = 0; i < hold; i++) order.Add(n - 1);
        }
        var plan = new AnimationPlan
        {
            Effect = spec.Effect, Frames = frames, Order = order, DelaysMs = Enumerable.Repeat(ms, order.Count).ToArray(), Size = look.Size,
            Level = look.Level, Notes = look.Notes,
        };
        return new AnimationRenderer(s, spec, plan, log) { _look = look };
    }

    // ------------------------------------------------------------------------------------------------ shared pieces

    /// <summary>The long edge <paramref name="edge"/> on a picture of <paramref name="w"/> × <paramref name="h"/>: the frame size with the
    /// picture's shape. 0 or a larger edge keeps the picture's size.</summary>
    static (int W, int H) FrameSize(int w, int h, int edge)
    {
        if (edge <= 0 || Math.Max(w, h) <= edge) return (w, h);
        double k = (double)edge / Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }

    /// <summary>The request of a look that reads the depth: the picture as the depth effects render it, with no depth effect.</summary>
    static ExportRequest DepthRequest(AnimationSpec spec, int level, (int W, int H)? size) =>
        BaseRequest(spec, level, size, true) with { DepthEffectNeedsDepth = true };

    /// <summary>Whether the depth the frames read is refined depth.</summary>
    static bool Refined(AnimationSpec spec, ExportState st) => spec.DepthRefined || st.EditedDepth?.Kind == DepthRefiner.StoredKind;

    /// <summary>The inverse distance (1/m) on a frame grid of <paramref name="size"/>: the depth on the grid of export level 1 (or of the
    /// frames' own level when that is finer), reduced by area.</summary>
    static float[] InverseDepthOn(CaptureSession s, AnimationSpec spec, int level, (int W, int H) size, CancellationToken cancel)
    {
        if (spec.SmoothDepth)
        {
            // smoothed along the picture on the grid of export level 2: one fixed size, so a small preview and the file show the same depth
            var e2 = s.PrepareExportSession(DepthRequest(spec, 2, null), cancel);
            var p2 = e2.ParallaxSource();
            if (p2.W >= size.W && p2.H >= size.H)
            {
                double focal1 = p2.Geometry.FocalPx * s.LevelSize(1).W / s.LevelSize(2).W;
                var smooth = DepthSmoother.Smooth(p2.Colour, p2.Depth, focal1 * DepthSmoother.StepBaselineMm, cancel);
                return LookMath.InverseDepth(smooth.V, p2.W, p2.H, size.W, size.H);
            }
        }
        int from = Math.Min(level, 1);
        var es = s.PrepareExportSession(DepthRequest(spec, from, null), cancel);
        var g = es.Grid.Size;
        if (g.W < size.W || g.H < size.H)
        {
            var own = s.PrepareExportSession(DepthRequest(spec, level, size), cancel);
            return LookMath.InverseDepth(own.DepthOnGrid(), size.W, size.H, size.W, size.H);
        }
        return LookMath.InverseDepth(es.DepthOnGrid(), g.W, g.H, size.W, size.H);
    }

    /// <summary>A point of the picture as shown (after a quarter turn of <paramref name="degrees"/> clockwise) in the picture as recorded.</summary>
    static (double X, double Y) Unturned((double X, double Y) p, int degrees) => degrees switch
    {
        90 => (p.Y, 1 - p.X), 180 => (1 - p.X, 1 - p.Y), 270 => (1 - p.Y, p.X), _ => p,
    };

    static string Pct(double v) => (v * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
    static string M(double mm) => (mm / 1000).ToString(mm >= 10_000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " m";

    // ------------------------------------------------------------------------------------------------ zoom-through

    /// <summary>The zoom-through: one render of the picture, its pyramid, and a window that closes in on the target.</summary>
    sealed class ZoomLook : Look
    {
        /// <summary>A window with less telephoto detail than this (dB) gains nothing from the last part of the zoom.</summary>
        const double MinDetailDb = 2.3;
        /// <summary>A subject nearer than this (mm) is out of the telephoto lenses' reach.</summary>
        const double MinSubjectMm = 1000;

        readonly ImagePyramid _pyramid; readonly ZoomPath _path;

        public ZoomLook(CaptureSession s, AnimationSpec spec, int n, Action<string>? log, CancellationToken cancel)
        {
            // the picture: the full-size render, or (a quick look at the path) the render of the level asked for
            bool full = spec.Level is null or 0;
            Level = full ? 0 : Math.Clamp(spec.Level!.Value, 0, 4);
            var es = s.PrepareExportSession(BaseRequest(spec, Level, null, false), cancel);
            var (w, h) = es.Grid.Size;
            var (_, px) = es.Rgba8();
            var picture = new Rgba(w, h, px);
            Size = FrameSize(w, h, spec.LongEdge);
            double unit = 1 << Level;                       // full-size pixels per pixel of this picture
            // the zoom ends on the own pixels of the full-size picture, for frames of the file's size (a preview's frames can be smaller)
            var file = FrameSize((int)(w * unit), (int)(h * unit), spec.ZoomEndEdge ?? spec.LongEdge);
            double endFull = 1.0;                           // full-size pixels per pixel of the file's last frame
            double winW = file.W / unit, winH = file.H / unit;   // the last frame's window at that scale, pixels of this picture

            // the subject's distance at a point: the median depth over the last frame's window
            double DistanceAt(double nx, double ny)
            {
                var d4 = s.PrepareExportSession(DepthRequest(spec, 4, null), cancel);
                var (gw, gh) = d4.Grid.Size; var z = d4.DepthOnGrid();
                int x0 = Math.Clamp((int)((nx - winW / w / 2) * gw), 0, gw - 1), x1 = Math.Clamp((int)((nx + winW / w / 2) * gw), x0, gw - 1);
                int y0 = Math.Clamp((int)((ny - winH / h / 2) * gh), 0, gh - 1), y1 = Math.Clamp((int)((ny + winH / h / 2) * gh), y0, gh - 1);
                var v = new List<float>();
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { float q = z[(long)y * gw + x]; if (q > 0f && float.IsFinite(q)) v.Add(q); }
                if (v.Count == 0) return 0;
                v.Sort();
                return v[v.Count / 2];
            }

            var lri = s.Lri;
            var vp = lri.ViewPreferencesBlock;
            // the light of the capture: gain × exposure time. Above 0.1 s the telephoto pictures are too noisy to add detail.
            double light = vp is not null && vp.HasImageGain && vp.HasImageIntegrationTimeNs ? vp.ImageGain * vp.ImageIntegrationTimeNs * 1e-9 : 0;
            bool dark = light >= 0.1 || s.Info.StackFrames > 1;
            (double X, double Y) target;
            string? early = null;
            if (full)
            {
                // the detail map: the full-size picture against the picture of level 1, which the wide lenses alone make
                var e1 = s.PrepareExportSession(BaseRequest(spec, 1, null, false), cancel);
                var map = DetailMap.Compute(picture, new Rgba(e1.Grid.Size.W, e1.Grid.Size.H, e1.Rgba8().Rgba), s.Options.Threads, cancel);
                if (spec.Target is { } t)
                {
                    target = (Math.Clamp(t.X, 0, 1) * w, Math.Clamp(t.Y, 0, 1) * h);
                    double gain = map.PooledGain(target.X - winW / 2, target.Y - winH / 2, winW, winH);
                    double dist = DistanceAt(target.X / w, target.Y / h);
                    log?.Invoke(FormattableString.Invariant($"zoom-through: target given, telephoto detail {gain:0.0} dB, distance {dist:0} mm"));
                    if (gain < MinDetailDb || dark)
                        early = dist > 0 && dist < MinSubjectMm ? $"The subject at this point is {M(dist)} away. The telephoto lenses cannot add detail to subjects nearer than 1 m."
                              : dark ? "This photo was taken in low light. The telephoto lenses add no detail to it."
                              : "The telephoto lenses add no detail at this point: it is outside their view, or it has no fine structure.";
                }
                else
                {
                    var pick = map.ChooseTarget(winW, winH) ?? throw new ExportRequestException("this picture is too small for a zoom-through at this size");
                    double gain = map.PooledGain(pick.X - winW / 2, pick.Y - winH / 2, winW, winH);
                    double dist = DistanceAt(pick.X / w, pick.Y / h);
                    log?.Invoke(FormattableString.Invariant($"zoom-through: target chosen at {pick.X / w:0.000},{pick.Y / h:0.000}, telephoto detail {gain:0.0} dB, distance {dist:0} mm"));
                    if (gain < MinDetailDb || dark)
                    {
                        string why = dark ? "it was taken in low light, so the telephoto lenses add no detail"
                                   : dist > 0 && dist < MinSubjectMm ? $"its subject is {M(dist)} away, nearer than the 1 m the telephoto lenses reach"
                                   : "the telephoto lenses add no detail anywhere in it";
                        throw new ExportRequestException($"This photo is not suited to a zoom-through: {why}. Choose the point to zoom onto yourself to make one anyway.");
                    }
                    target = (pick.X, pick.Y);
                    Notes.Add($"The zoom ends at {Pct(pick.X / w)} from the left, {Pct(pick.Y / h)} from the top: the telephoto lenses add the most detail there.");
                }
                if (early is not null) { endFull = 2.0; Notes.Add(early + " The zoom ends at half the full size, where a wide lens alone is as sharp."); }
            }
            else
            {
                var t = spec.Target ?? (0.5, 0.5);
                target = (Math.Clamp(t.X, 0, 1) * w, Math.Clamp(t.Y, 0, 1) * h);
                double dist = DistanceAt(target.X / w, target.Y / h);
                if (dist > 0 && dist < MinSubjectMm) Notes.Add($"The subject at this point is {M(dist)} away. The telephoto lenses cannot add detail to subjects nearer than 1 m.");
                else if (dark) Notes.Add("This photo was taken in low light. The telephoto lenses add little detail to it.");
                if (spec.Target is null) Notes.Add("This is a quick look at the zoom, onto the middle of the picture. The file is made from the full-size picture, and its target is chosen there.");
            }
            _pyramid = ImagePyramid.Build(picture, 64, cancel);
            _path = new ZoomPath(w, h, Size.W, Size.H, target.X, target.Y, n, endFull * winW / Size.W);
            if (full && _path.Zoom < 1.5) Notes.Add("At this frame size the whole picture is close to full size already: the zoom is short. A smaller frame zooms farther.");
        }

        public override string Label(int k, int n) => string.Format(CultureInfo.InvariantCulture, "zoom {0:0.0}×", _path.StartScale / _path.At(k).Scale);

        public override Rgba Render(int k, int n, CancellationToken cancel)
        {
            var (scale, x0, y0) = _path.At(k);
            return _pyramid.Sample(x0, y0, scale, Size.W, Size.H);
        }
    }

    // ------------------------------------------------------------------------------------------------ fog and slice reveal

    /// <summary>The picture and the inverse distance of the looks that change the picture per pixel.</summary>
    static (ExportSession Es, Rgba Picture, float[] InverseDepth, int Level) PictureAndDepth(CaptureSession s, AnimationSpec spec, CancellationToken cancel)
    {
        var (level, size, renderSize) = Grid(s, spec, depth: true, cancel);
        var es = s.PrepareExportSession(DepthRequest(spec, level, renderSize), cancel);
        var (_, px) = es.Rgba8();
        var q = InverseDepthOn(s, spec, level, size, cancel);
        return (es, new Rgba(size.W, size.H, px), q, level);
    }

    sealed class FogFrames : Look
    {
        readonly FogLook _fog;

        public FogFrames(CaptureSession s, AnimationSpec spec, CancellationToken cancel)
        {
            var (es, picture, q, level) = PictureAndDepth(s, spec, cancel);
            Level = level; Size = (picture.W, picture.H);
            var tone = ToneCurve.Fit(picture.P, es.FloatImage(), picture.W, picture.H);
            _fog = new FogLook(picture, q, tone, spec.FogMode, spec.FogDensity, spec.FogColor);
            if (!spec.SmoothDepth) Notes.Add("Fog follows the depth map closely: without smoothing, it shows the steps of the depth map on smooth surfaces.");
        }

        public override string Label(int k, int n)
        {
            var (density, front) = _fog.At((double)k / (n - 1));
            return front > 0 ? "fog from " + M(front * 1000) : string.Format(CultureInfo.InvariantCulture, "fog {0:0} %", 100 * density / Math.Max(_fog.Density, 1e-12));
        }

        public override Rgba Render(int k, int n, CancellationToken cancel) => _fog.Frame((double)k / (n - 1));
    }

    sealed class SliceFrames : Look
    {
        readonly SliceLook _slices;

        public SliceFrames(CaptureSession s, AnimationSpec spec, CancellationToken cancel)
        {
            var (es, picture, q, level) = PictureAndDepth(s, spec, cancel);
            Level = level; Size = (picture.W, picture.H);
            double focus = 0;
            if (spec.SliceOrder == SliceOrder.FromFocus)
            {
                try { focus = DofModel.DefaultFocusDepth(s.Lri, es.State.DepthCache(es.Grid.Window).Current) / 1000.0; }
                catch (InvalidOperationException) { focus = 0; }
            }
            _slices = new SliceLook(picture, q, spec.SliceOrder, spec.SliceSoftness, focus);
            if (!spec.SmoothDepth) Notes.Add("The reveal follows the depth map closely: without smoothing, flat surfaces can come in in patches.");
        }

        public override string Label(int k, int n) => string.Format(CultureInfo.InvariantCulture, "revealed {0:0} %", 100 * LookMath.Ease((double)k / (n - 1)));
        public override Rgba Render(int k, int n, CancellationToken cancel) => _slices.Frame((double)k / (n - 1));
    }

    // ------------------------------------------------------------------------------------------------ pan-and-zoom

    sealed class PanLook : Look
    {
        /// <summary>The automatic travel aims at this much parallax at most (share of the frame width).</summary>
        const double AutoParallax = 0.05;
        /// <summary>The source is this many times the frame: the last frame's crop then has one source pixel per frame pixel.</summary>
        const double SourceOverFrame = PanZoomMove.ZoomEnd;
        /// <summary>The size the preflight runs at (long edge, pixels): the size its limits were set at.</summary>
        const int PreflightEdge = 2608;

        PanZoomSource _src; readonly PanZoomMove _move; readonly int _turn;

        public PanLook(CaptureSession s, AnimationSpec spec, Action<string>? log, CancellationToken cancel)
        {
            // the source: the parallax effects' picture and depth, from the smallest level that gives the last frame its own pixels
            int level;
            if (spec.Level is int l) level = Math.Clamp(l, 0, 4);
            else
            {
                level = 0;
                for (int L = 4; L >= 0; L--)
                {
                    var (lw, lh) = s.LevelSize(L);
                    if (spec.LongEdge > 0 && Math.Max(lw, lh) >= spec.LongEdge * SourceOverFrame) { level = L; break; }
                }
                if (spec.LongEdge <= 0) level = 2;
            }
            Level = level;
            var like = spec with { Effect = AnimationEffect.ParallaxSweep, SingleView = true };
            var es = s.PrepareExportSession(BaseRequest(like, level, null, true), cancel);
            var ps = es.ParallaxSource();
            double focal1 = ps.Geometry.FocalPx * s.LevelSize(1).W / s.LevelSize(level).W;
            var native = new PanZoomSource(ps.Colour, ps.Depth, ps.Geometry.FocalPx, focal1);
            var frame = spec.LongEdge > 0 ? FrameSize(native.W, native.H, spec.LongEdge) : (native.W, native.H);
            _src = spec.LongEdge > 0 ? native.Scaled((int)Math.Ceiling(Math.Max(frame.Item1, frame.Item2) * SourceOverFrame)) : native;
            _turn = spec.TurnDegrees;
            Size = spec.Turned ? (frame.Item2, frame.Item1) : frame;

            // the target as recorded; the camera's focus point when none is given
            var af = DofModel.AfPoint(s.Lri);
            var t = spec.Target is { } given ? Unturned(given, _turn) : (Math.Clamp(af.X, 0.05, 0.95), Math.Clamp(af.Y, 0.05, 0.95));
            // the depth for the move: smoothed along the picture on the grid of export level 2 (one fixed size, so a small preview and the
            // file move the same scene), or the photo's depth as it is
            Plane moveDepth; double moveFocal;
            if (spec.SmoothDepth)
            {
                var e2 = level == 2 ? es : s.PrepareExportSession(BaseRequest(like, 2, null, true), cancel);
                var p2 = e2.ParallaxSource();
                moveFocal = p2.Geometry.FocalPx;
                moveDepth = DepthSmoother.Smooth(p2.Colour, p2.Depth, focal1 * DepthSmoother.StepBaselineMm, cancel);
                _src = new PanZoomSource(_src.Colour, moveDepth.ResizeNearest(_src.W, _src.H), _src.FocalPx, focal1);
            }
            else
            {
                var e1 = level == 1 ? es : s.PrepareExportSession(BaseRequest(like, 1, null, true), cancel);
                moveDepth = new Plane(e1.Grid.Size.W, e1.Grid.Size.H, e1.DepthOnGrid()); moveFocal = focal1;
            }
            // the scene's distances and the preflight come from that depth alone, at one fixed size. The warp needs a picture; a blank one serves.
            var blank = new Rgba(moveDepth.W, moveDepth.H);
            for (long p = 3; p < blank.P.LongLength; p += 4) blank.P[p] = 255;
            var small = new PanZoomSource(blank, moveDepth, moveFocal, focal1).Scaled(PreflightEdge);
            double near = small.NearMm(), far = small.DepthPercentile(0.9), hold = small.DepthAt(t.Item1 * small.W, t.Item2 * small.H);
            if (!(hold > 0)) hold = small.DepthPercentile(0.5);
            var move = new PanZoomMove { TargetX = t.Item1, TargetY = t.Item2, HoldMm = hold, NearMm = near, Vertical = _turn is 90 or 270 };
            bool refined = Refined(spec, es.State);
            string advice = refined ? "" : " Refined depth follows the outlines of things more closely and can raise this limit.";
            if (spec.Flat)
            {
                _move = move with { Flat = true };
                Notes.Add("A flat pan and zoom: nothing in the scene slides.");
                return;
            }
            // the preflight: how far the camera can travel before the picture tears
            var pre = small.Preflight(move, cancel);
            foreach (var (tr, st) in pre.Tried)
                log?.Invoke(FormattableString.Invariant($"pan-and-zoom preflight: travel {tr:0.00} tears {st.TearPct:0.00} % hidden {st.EdgePct:0.00} % gap {st.GapPct:0.00} % smear {st.SmearPct:0.00} % {(st.Acceptable ? "ok" : "fails")}"));
            double perTravel = PanZoomMove.Parallax(1.0, small.FocalPx / small.W, near, far);
            string limitText = pre.Limit > 0
                ? $"The camera can travel up to {Pct(pre.Limit)} of the nearest distance ({M(near)}) on this scene: {(pre.Limit * near).ToString("0", CultureInfo.InvariantCulture)} mm sideways"
                  + (pre.FirstFailure.Length > 0 ? $". Past that, {pre.FirstFailure}." : ".")
                : $"With this depth map the picture fails at the smallest camera travel ({pre.FirstFailure}). The nearest content is {M(near)} away.";
            if (spec.Travel is double asked)
            {
                _move = move with { Travel = Math.Clamp(asked, 0, 0.5) };
                Notes.Add(limitText);
                if (asked > pre.Limit) Notes.Add($"The travel of {Pct(asked)} is past that limit: expect torn or smeared edges." + advice);
            }
            else if (pre.Limit <= 0)
            {
                _move = move with { Flat = true };
                Notes.Add(limitText + " The clip is a flat pan and zoom." + advice);
            }
            else
            {
                double travel = Math.Min(pre.Limit, AutoParallax / perTravel);
                _move = move with { Travel = travel };
                Notes.Add(limitText + (pre.Limit < 0.02 && !refined ? advice : ""));
            }
            if (!_move.Flat)
                Notes.Add(string.Format(CultureInfo.InvariantCulture, "Camera travel: {0:0} mm sideways and {1:0} mm forward. Near content slides about {2:0.0} % of the frame width against far content.",
                                        _move.SidewaysMm, _move.ForwardMm, 100 * _move.Travel * perTravel));
            if (s.SavedTransform is not null) Notes.Add("The photo's crop and rotation are not applied to a pan-and-zoom animation.");
        }

        public override string Label(int k, int n) => string.Format(CultureInfo.InvariantCulture, "zoom {0:0.00}×", _move.At((double)k / (n - 1), _src.W, _src.H).Zoom);

        public override Rgba Render(int k, int n, CancellationToken cancel)
        {
            var (fw, fh) = _turn is 90 or 270 ? (Size.H, Size.W) : Size;
            return TurnRgba(_src.Frame(_move, (double)k / (n - 1), fw, fh), _turn);
        }
    }

    // ------------------------------------------------------------------------------------------------ tilt-shift

    /// <summary>The tilt-shift sweep: the depth effect at a very wide aperture, rendered at a few focus distances. A frame is the blend of
    /// the two renders around its focus, with the stronger color and contrast of a miniature.</summary>
    sealed class TiltLook : Look
    {
        /// <summary>The number of focus distances rendered.</summary>
        const int Steps = 10;
        /// <summary>The automatic f-number per 1/m of the scene's range of inverse distance.</summary>
        const double FNumberPerRange = 0.45;

        readonly CaptureSession _s; readonly AnimationSpec _spec; readonly (int W, int H)? _renderSize; readonly float _fnum;
        readonly float[] _focus = new float[Steps]; readonly Rgba?[] _renders = new Rgba?[Steps];
        readonly DepthImageCache? _tilted; readonly ExportState _state; readonly DofRendering _rendering;
        readonly object _gate = new();

        public TiltLook(CaptureSession s, AnimationSpec spec, CancellationToken cancel)
        {
            _s = s; _spec = spec;
            var (level, size, renderSize) = Grid(s, spec, depth: true, cancel);
            Level = level; Size = size; _renderSize = renderSize;
            var es = s.PrepareExportSession(DepthRequest(spec, level, renderSize), cancel);
            _state = es.State; _rendering = es.Request.DepthEffectRendering;
            var q = InverseDepthOn(s, spec, level, size, cancel);
            Array.Sort(q);
            double near = LookMath.Percentile(q, 0.95), far = LookMath.Percentile(q, 0.08);   // 1/m
            for (int k = 0; k < Steps; k++) _focus[k] = (float)Math.Round(1000.0 / (near + (far - near) * k / (Steps - 1)), 1);
            float aif = s.Info.AllInFocusFNumber;
            _fnum = spec.MiniatureFNumber ?? (float)Math.Round(Math.Clamp(FNumberPerRange * (near - far), 0.02, 2.0), 2);
            if (!(_fnum > 0)) throw new ExportRequestException("the f-number must be greater than 0");
            if (!(_fnum < aif)) Notes.Add(string.Format(CultureInfo.InvariantCulture, "f/{0:0.##} is not below this photo's all-in-focus f/{1:0.##}: nothing will be blurred", _fnum, aif));
            Notes.Add(string.Format(CultureInfo.InvariantCulture, "Depth effect at f/{0:0.##}, focus from {1} to {2}.", _fnum, M(_focus[0]), M(_focus[^1])));

            if (spec.Tilt != 0)
            {
                // the tilted focal plane: the inverse distance of each depth pixel shifted by its place down the picture as shown, so that one
                // focus distance is in focus farther away towards the bottom
                var win = es.Grid.Window;
                var cache = _state.DepthCache(win).Current;
                var dense = cache.ToDense();
                int lv = DepthEditor.EditLevel(win);
                var (pw, ph) = win.ExportDims[lv]; var (ox, oy) = win.Origins[lv];
                double g = (near - far) * spec.Tilt / 1000.0;   // 1/mm over the picture's height
                int turn = spec.TurnDegrees, cw = cache.W, chh = cache.H;
                var tilted = new float[dense.Length];
                Parallel.For(0, chh, y =>
                {
                    double v = (double)(y - oy) / ph;
                    for (int x = 0; x < cw; x++)
                    {
                        double u = (double)(x - ox) / pw;
                        double down = turn switch { 90 => u, 180 => 1 - v, 270 => 1 - u, _ => v };
                        float z = dense[(long)y * cw + x];
                        double inv = 1.0 / Math.Max(z, 1f) + g * (down - 0.5);
                        tilted[(long)y * cw + x] = (float)(1.0 / Math.Max(inv, 1e-6));
                    }
                });
                _tilted = DepthImageCache.FromFullDepth(tilted, cw, chh, win);
            }
        }

        Rgba RenderStep(int k, CancellationToken cancel)
        {
            lock (_gate)
            {
                if (_renders[k] is { } hit) return hit;
                var req = DepthRequest(_spec, Level, _renderSize) with { DepthEffectFNumber = _fnum, FocusDepthMm = _focus[k] };
                if (_tilted is not null)
                    req = req with { DepthEffectCache = DofTileCache.ForCapture(_s.Lri, _state.Cache, _tilted, _fnum, _focus[k], _rendering) };
                var es = _s.PrepareExportSession(req, cancel);
                var (_, px) = es.Rgba8();
                return _renders[k] = new Rgba(es.Grid.Size.W, es.Grid.Size.H, px);
            }
        }

        (int K, double Blend) StepOf(int k, int n)
        {
            double u = LookMath.Ease((double)k / (n - 1)) * (Steps - 1);
            int k0 = Math.Min((int)Math.Floor(u), Steps - 2);
            return (k0, u - k0);
        }

        public override string Label(int k, int n)
        {
            var (k0, a) = StepOf(k, n);
            double z = 1.0 / ((1 - a) / _focus[k0] + a / _focus[k0 + 1]);
            return string.Format(CultureInfo.InvariantCulture, "f/{0:0.##} · focus {1}", _fnum, M(z));
        }

        public override Rgba Render(int k, int n, CancellationToken cancel)
        {
            var (k0, blend) = StepOf(k, n);
            var a = RenderStep(k0, cancel); var b = blend > 0 ? RenderStep(k0 + 1, cancel) : a;
            return LookMath.Miniature(a, b, (float)blend);
        }

    }
}
