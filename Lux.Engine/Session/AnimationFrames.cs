using System.Globalization;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Session;

/// <summary>The animated effects (a Lux feature: Lumen has no animated output).</summary>
public enum AnimationEffect
{
    /// <summary>The wide lenses' own pictures, one after another: the physical wigglegram (28 mm photos only).</summary>
    Wigglegram,
    /// <summary>A virtual camera moving along a short path, drawn from the photo and its depth map (the other wide lenses fill in what the
    /// main view cannot see).</summary>
    ParallaxSweep,
    /// <summary>Lumen's depth effect with the focus pulled from one distance to another at a fixed aperture.</summary>
    RackFocus,
    /// <summary>Lumen's depth effect opening from all in focus to a wide aperture at a fixed focus.</summary>
    ApertureSweep,
    /// <summary>Every lens of the capture in turn (wide, 70 mm and 150 mm views).</summary>
    LensTour,
}

/// <summary>Paths of the parallax sweep.</summary>
public enum AnimationPath { Sweep, Arc, Line, Orbit }

/// <summary>
/// An animation, as data. Every frame comes from the capture's export renders: the depth-effect frames are exactly the stills `convert`
/// writes with <c>--f-number</c> / <c>--focus-depth</c> at the same level and size; the parallax and wigglegram frames are exactly the
/// frames of `convert --formats parallax-wiggle-interp | parallax-orbit | parallax-single | parallax-wiggle` with the same parameters.
/// </summary>
public sealed record AnimationSpec
{
    public AnimationEffect Effect { get; init; } = AnimationEffect.RackFocus;
    /// <summary>Distinct frames (rack focus, aperture sweep, parallax sweep). The wigglegram and the lens tour have one per lens.</summary>
    public int Frames { get; init; } = 24;
    /// <summary>Time each frame is shown, ms; 0 = the effect's default (<see cref="DefaultFrameMs"/>).</summary>
    public int FrameMs { get; init; }
    /// <summary>Play forward then back (no repeated end frames); null = the effect's default (<see cref="DefaultPingPong"/>).</summary>
    public bool? PingPong { get; init; }
    /// <summary>Long edge of the frames, px; 0 = the render level's own size.</summary>
    public int LongEdge { get; init; } = 1040;
    /// <summary>The export level the frames are rendered from (0 = full size … 4); null = the smallest level at least <see cref="LongEdge"/>
    /// on its long edge (no enlarging).</summary>
    public int? Level { get; init; }
    /// <summary>Rack focus: the aperture; aperture sweep: the widest aperture it opens to.</summary>
    public float FNumber { get; init; } = 2.0f;
    /// <summary>Aperture sweep: where it starts; null = the capture's all-in-focus f-number (no blur).</summary>
    public float? FNumberFrom { get; init; }
    /// <summary>Rack focus: the focus distances it pulls between, mm; null = the nearer (10th percentile) / farther (90th) depth.</summary>
    public float? FocusFromMm { get; init; }
    public float? FocusToMm { get; init; }
    /// <summary>Aperture sweep: the focus distance, mm; null = Lumen's default focus.</summary>
    public float? FocusMm { get; init; }
    public AnimationPath Path { get; init; } = AnimationPath.Sweep;
    /// <summary>Parallax sweep: how far the virtual camera travels, mm, end to end (71.49 = the widest real baseline of the wide lenses).</summary>
    public double BaselineMm { get; init; } = 71.49;
    /// <summary>Parallax sweep: the distance that stays still, metres as text, or "auto" (the median depth).</summary>
    public string Converge { get; init; } = "auto";
    /// <summary>Parallax sweep from the main view alone (gaps painted in), without the other wide lenses' pictures.</summary>
    public bool SingleView { get; init; }
    /// <summary>The capture's saved edits and settings (depth edits, tone, crop) apply to the depth-effect and parallax frames.</summary>
    public bool UseSavedEdits { get; init; } = true;
    public string? EditsVariant { get; init; }
    /// <summary>Module-ISP config level for the wigglegram and lens tour (0 = full resolution, the export's; a preview uses 2).</summary>
    public int LensIspLevel { get; init; }
    /// <summary>A photo transform for the depth-effect frames in place of the saved one (exactly as <c>convert --transform</c>): the held turn
    /// of a photo whose exports follow the way the camera was held; null = the saved settings decide.</summary>
    public ExportTransform? Transform { get; init; }
    /// <summary>Degrees clockwise (0, 90, 180, 270) the parallax, wigglegram and lens-tour frames are turned after they are made (a pixel
    /// permutation; those effects never read the saved transform): the photo's orientation as shown.</summary>
    public int OutputTurn { get; init; }
    int Turn => ((OutputTurn % 360) + 360) % 360;
    internal bool Turned => Turn is 90 or 270;
    internal int TurnDegrees => Turn;

    public static int DefaultFrameMs(AnimationEffect e) => e switch
    {
        AnimationEffect.Wigglegram => 100, AnimationEffect.ParallaxSweep => 60, AnimationEffect.LensTour => 500, _ => 70,
    };
    public static bool DefaultPingPong(AnimationEffect e, AnimationPath p) => e switch
    {
        AnimationEffect.LensTour => false, AnimationEffect.ParallaxSweep => p != AnimationPath.Orbit, _ => true,
    };
    public int EffectiveFrameMs => FrameMs > 0 ? FrameMs : DefaultFrameMs(Effect);
    public bool EffectivePingPong => PingPong ?? DefaultPingPong(Effect, Path);
}

/// <summary>Whether an effect can be made from a capture, and why not.</summary>
public sealed record AnimationEffectCheck(AnimationEffect Effect, bool Available, string? Reason, string? Note);

/// <summary>One distinct frame of a plan.</summary>
public sealed record AnimationFrameInfo(int Index, string Label)
{
    public float? FNumber { get; init; }
    public float? FocusMm { get; init; }
    public string? Module { get; init; }
    public double Tx { get; init; }
    public double Ty { get; init; }
}

/// <summary>An animation resolved against a capture: its distinct frames, the order they play in and for how long, the frame size.</summary>
public sealed record AnimationPlan
{
    public required AnimationEffect Effect { get; init; }
    public required IReadOnlyList<AnimationFrameInfo> Frames { get; init; }
    /// <summary>The played sequence: indices into <see cref="Frames"/>.</summary>
    public required IReadOnlyList<int> Order { get; init; }
    public required IReadOnlyList<int> DelaysMs { get; init; }
    public required (int W, int H) Size { get; init; }
    /// <summary>The export level the frames come from (-1: the lenses' own pictures).</summary>
    public int Level { get; init; }
    /// <summary>The explicit output size passed to the render (null = the level's own size).</summary>
    public (int W, int H)? RenderSize { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DelaysMs.Sum());
}

/// <summary>
/// Renders an animation's frames from a <see cref="CaptureSession"/>: the depth-effect frames through the export's own JPEG render
/// (<see cref="CaptureSession.PrepareExportSession"/>, so saved edits, tone settings, crop and the depth version apply as in an export), the
/// parallax frames from the export's parallax source and the real wide-lens donors, the wigglegram and lens tour from the module renders.
/// Frames are rendered one at a time (each one in parallel inside); cancellation is checked between frames and inside the renders.
/// What frames have in common (the parallax source, the lens pictures) is made once per renderer.
/// </summary>
public sealed class AnimationRenderer
{
    public CaptureSession Session { get; }
    public AnimationSpec Spec { get; }
    public AnimationPlan Plan { get; }
    readonly Action<string>? _log;
    readonly object _gate = new();

    AnimationRenderer(CaptureSession s, AnimationSpec spec, AnimationPlan plan, Action<string>? log) { Session = s; Spec = spec; Plan = plan; _log = log; }

    // ------------------------------------------------------------------------------------------------ what a capture can do

    /// <summary>Which effects this capture can make, with a plain reason when one cannot.</summary>
    public static IReadOnlyList<AnimationEffectCheck> Check(CaptureSession s)
    {
        var lri = s.Lri;
        int wide = Wigglegram.SelectModules(lri, 0, includeMono: false).Count;
        bool anyWide = lri.Modules.Any(m => Wigglegram.Group((int)m.Value.Module.Id) == 0);
        string tele = $"This photo was taken with the {lri.Header.ImageFocalLength} mm lenses only";
        return new[]
        {
            wide >= 2
                ? new AnimationEffectCheck(AnimationEffect.Wigglegram, true, null, $"{wide} wide lenses" + (s.Info.StackFrames > 1 ? "; a night photo uses the first picture of each lens" : ""))
                : new AnimationEffectCheck(AnimationEffect.Wigglegram, false,
                    anyWide ? "This photo has fewer than two colour wide lenses."
                            : tele + ": they look at different parts of the scene, so there are no side-by-side views to wiggle between. Wigglegrams need a wide (28 mm) photo.", null),
            new AnimationEffectCheck(AnimationEffect.ParallaxSweep, true, null,
                wide >= 2 ? "The other wide lenses fill in what the main view cannot see."
                          : tele + ": the movement is drawn from one view and gaps are painted in, so keep it small."),
            new AnimationEffectCheck(AnimationEffect.RackFocus, true, null, "Uses the depth effect exactly as an export does."),
            new AnimationEffectCheck(AnimationEffect.ApertureSweep, true, null, "Uses the depth effect exactly as an export does."),
            new AnimationEffectCheck(AnimationEffect.LensTour, true, null, $"{lri.Modules.Count} lenses"),
        };
    }

    // ------------------------------------------------------------------------------------------------ planning

    /// <summary>Resolve <paramref name="spec"/> against the capture (the defaults that depend on its depth, the level and size) and return a
    /// renderer for it. The depth effects and the parallax sweep need the depth map: when the capture has not been analysed this runs the
    /// analysis (about a minute) unless the analysis cache holds it.</summary>
    public static async Task<AnimationRenderer> CreateAsync(CaptureSession s, AnimationSpec spec, Action<string>? log = null, CancellationToken cancel = default)
    {
        var check = Check(s).First(c => c.Effect == spec.Effect);
        if (!check.Available) throw new ExportRequestException(check.Reason ?? "this capture cannot make this animation");
        int n = Math.Clamp(spec.Frames, 2, 240);
        var notes = new List<string>();
        var inv = CultureInfo.InvariantCulture;
        var frames = new List<AnimationFrameInfo>();
        int level = -1; (int W, int H) size; (int W, int H)? renderSize = null;
        switch (spec.Effect)
        {
            case AnimationEffect.RackFocus:
            case AnimationEffect.ApertureSweep:
            {
                // the depth first (the analysis, or the kept geometry / an imported session's own): every frame reads it
                await Task.Run(() => s.DepthState(), cancel).ConfigureAwait(false);
                (level, size, renderSize) = await Task.Run(() => Grid(s, spec, depth: true, cancel), cancel).ConfigureAwait(false);
                float aif = s.Info.AllInFocusFNumber;
                if (spec.Effect == AnimationEffect.RackFocus)
                {
                    float z1, z2;
                    if (spec.FocusFromMm is float a && spec.FocusToMm is float b) { z1 = a; z2 = b; }
                    else
                    {
                        var (p10, p90) = await DepthPercentilesAsync(s, cancel).ConfigureAwait(false);
                        z1 = spec.FocusFromMm ?? p10; z2 = spec.FocusToMm ?? p90;
                    }
                    if (!(z1 > 0) || !(z2 > 0)) throw new ExportRequestException("the focus distances must be greater than 0");
                    float fn = Round2(spec.FNumber);
                    if (!(fn < aif)) notes.Add(string.Format(inv, "f/{0:0.##} is not below this photo's all-in-focus f/{1:0.##}: nothing will be blurred", fn, aif));
                    for (int k = 0; k < n; k++)
                    {
                        double t = (double)k / (n - 1), e = 0.5 - 0.5 * WineCrt.Cos(Math.PI * t);
                        float z = (float)Math.Round(1.0 / ((1 - e) / z1 + e / z2), 1);
                        frames.Add(new AnimationFrameInfo(k, string.Format(inv, "f/{0:0.#} · focus {1:0.00} m", fn, z / 1000)) { FNumber = fn, FocusMm = z });
                    }
                }
                else
                {
                    float from = spec.FNumberFrom ?? aif, to = Round2(spec.FNumber);
                    if (!(to > 0) || !(from > 0)) throw new ExportRequestException("the f-numbers must be greater than 0");
                    float? focus = spec.FocusMm is float fm ? (float)Math.Round(fm, 1) : null;
                    for (int k = 0; k < n; k++)
                    {
                        double t = (double)k / (n - 1), e = 0.5 - 0.5 * WineCrt.Cos(Math.PI * t);
                        float N = k == 0 ? from : Round2((float)(1.0 / ((1 - e) / from + e / to)));
                        float? fnum = N >= aif ? null : N;   // at or above the all-in-focus f-number the picture is unchanged: no depth effect at all
                        frames.Add(new AnimationFrameInfo(k, fnum is null ? "all in focus" : string.Format(inv, "f/{0:0.#}", N)) { FNumber = fnum, FocusMm = focus });
                    }
                }
                break;
            }
            case AnimationEffect.ParallaxSweep:
            {
                await Task.Run(() => s.DepthState(), cancel).ConfigureAwait(false);
                (level, size, renderSize) = await Task.Run(() => Grid(s, spec, depth: true, cancel), cancel).ConfigureAwait(false);
                // the frame size is the parallax source scaled to the long edge (ParallaxSource.Scaled)
                var nat = size;
                if (spec.LongEdge > 0 && Math.Max(nat.W, nat.H) > spec.LongEdge)
                {
                    double kk = (double)spec.LongEdge / Math.Max(nat.W, nat.H);
                    size = (Math.Max(1, (int)Math.Round(nat.W * kk)), Math.Max(1, (int)Math.Round(nat.H * kk)));
                }
                renderSize = null;
                if (spec.Turned) size = (size.H, size.W);
                for (int k = 0; k < n; k++) frames.Add(new AnimationFrameInfo(k, $"view {k + 1} of {n}"));
                if (s.SavedTransform is not null) notes.Add("The photo's crop and rotation are not applied to a parallax animation.");
                break;
            }
            case AnimationEffect.Wigglegram:
            {
                var order = Wigglegram.SelectModules(s.Lri, 0, includeMono: false);
                for (int k = 0; k < order.Count; k++) frames.Add(new AnimationFrameInfo(k, order[k]) { Module = order[k] });
                size = LensFrameSize(s, spec);
                if (spec.Turned) size = (size.H, size.W);
                notes.Add("Made from the wide lenses' own pictures: tone and depth edits do not apply.");
                break;
            }
            default:
            {
                var mods = s.Lri.Modules.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                for (int k = 0; k < mods.Count; k++) frames.Add(new AnimationFrameInfo(k, mods[k]) { Module = mods[k] });
                size = LensFrameSize(s, spec);
                if (spec.Turned) size = (size.H, size.W);
                notes.Add("Made from each lens's own picture: tone and depth edits do not apply.");
                break;
            }
        }
        var order2 = Enumerable.Range(0, frames.Count).ToList();
        if (spec.EffectivePingPong && frames.Count >= 3) for (int i = frames.Count - 2; i >= 1; i--) order2.Add(i);
        int ms = Math.Max(10, spec.EffectiveFrameMs);
        var plan = new AnimationPlan
        {
            Effect = spec.Effect, Frames = frames, Order = order2, DelaysMs = Enumerable.Repeat(ms, order2.Count).ToArray(), Size = size,
            Level = level, RenderSize = renderSize, Notes = notes,
        };
        return new AnimationRenderer(s, spec, plan, log);
    }

    static float Round2(float v) => (float)Math.Round(v, 2);

    /// <summary>The render level and frame size of a depth-based animation: the level's grid after the saved crop / rotation (as an export
    /// lays it out), then scaled to the long edge.</summary>
    static (int Level, (int W, int H) Size, (int W, int H)? RenderSize) Grid(CaptureSession s, AnimationSpec spec, bool depth, CancellationToken cancel)
    {
        (int W, int H) Natural(int L) => s.PrepareExportSession(BaseRequest(spec, L, null, depth), cancel).Grid.Size;
        int level;
        if (spec.Level is int l) level = Math.Clamp(l, 0, 4);
        else
        {
            level = 0;
            for (int L = 4; L >= 0; L--)
            {
                var (w, h) = s.LevelSize(L);
                if (spec.LongEdge > 0 && Math.Max(w, h) >= spec.LongEdge) { level = L; break; }
            }
            if (spec.LongEdge <= 0) level = 2;
        }
        var nat = spec.Effect == AnimationEffect.ParallaxSweep ? s.LevelSize(level) : Natural(level);
        if (spec.Effect == AnimationEffect.ParallaxSweep || spec.LongEdge <= 0 || Math.Max(nat.W, nat.H) <= spec.LongEdge) return (level, nat, null);
        double k = (double)spec.LongEdge / Math.Max(nat.W, nat.H);
        var sz = (Math.Max(1, (int)Math.Round(nat.W * k)), Math.Max(1, (int)Math.Round(nat.H * k)));
        return (level, sz, sz);
    }

    /// <summary>The request a frame renders through (the JPG export's render, nothing written).</summary>
    static ExportRequest BaseRequest(AnimationSpec spec, int level, (int W, int H)? size, bool depth)
    {
        var over = new HashSet<string> { "f-number", "focus" };
        if (spec.Effect == AnimationEffect.ParallaxSweep || spec.Transform is not null) over.Add("transform");
        return new ExportRequest
        {
            Transform = spec.Effect == AnimationEffect.ParallaxSweep ? null : spec.Transform,
            Formats = spec.Effect == AnimationEffect.ParallaxSweep ? Array.Empty<ExportImageFormat>() : new[] { ExportImageFormat.Jpeg },
            Level = level, Size = size, UseSavedEdits = spec.UseSavedEdits, EditsVariant = spec.UseSavedEdits ? spec.EditsVariant : null,
            OverriddenSettings = over,
            Parallax = spec.Effect == AnimationEffect.ParallaxSweep ? ParallaxRequestOf(spec, 0) : null,
            // a request that reads the depth: the level-0 state is what every frame of the effect uses (the depth effect forces it anyway)
            DepthEffectNeedsDepth = false,
        };
    }

    static ParallaxRequest ParallaxRequestOf(AnimationSpec spec, int longEdge) => new()
    {
        Formats = new[] { spec.Path == AnimationPath.Orbit ? ParallaxFormat.Orbit : spec.SingleView ? ParallaxFormat.Single : ParallaxFormat.WiggleInterp },
        Size = longEdge, Frames = Math.Clamp(spec.Frames, 2, 240), Fill = "donors",
        Path = spec.Path switch { AnimationPath.Arc => "arc", AnimationPath.Line => "line", _ => "sweep" },
        Baseline = spec.BaselineMm, Converge = spec.Converge,
        Loop = spec.EffectivePingPong ? "pingpong" : "forward",
    };

    /// <summary>The frame size of the lens animations at full resolution (a preview at another module-ISP level may come out smaller; its frames
    /// carry their own size).</summary>
    static (int W, int H) LensFrameSize(CaptureSession s, AnimationSpec spec)
    {
        var (w, h) = s.Info.SensorSize;
        if (spec.LongEdge > 0 && Math.Max(w, h) > spec.LongEdge) { int step = (int)Math.Ceiling(Math.Max(w, h) / (double)spec.LongEdge); return (w / step, h / step); }
        return (w, h);
    }

    /// <summary>The 10th and 90th percentile of the scene depth (mm) — the default ends of a rack focus.</summary>
    public static async Task<(float P10, float P90)> DepthPercentilesAsync(CaptureSession s, CancellationToken cancel = default)
    {
        var d = await Task.Run(() => { var st = s.DepthState(); return st.DepthCache(s.Window).Current; }, cancel).ConfigureAwait(false);
        var v = d.ToDense().Where(x => x > 0 && float.IsFinite(x) && x < DepthImageCache.Clip).ToArray();
        if (v.Length == 0) throw new ExportRequestException("this photo's depth map is empty");
        Array.Sort(v);
        return (v[(int)(0.1 * (v.Length - 1))], v[(int)(0.9 * (v.Length - 1))]);
    }

    // ------------------------------------------------------------------------------------------------ frames

    /// <summary>The `lux-light convert` options that write distinct frame <paramref name="k"/> of a depth-effect animation as a still (the
    /// proof of the frames: that JPG encodes exactly these pixels).</summary>
    public string ConvertArgs(int k)
    {
        var f = Plan.Frames[k]; var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "--level", Plan.Level.ToString(inv) };
        if (Plan.RenderSize is { } sz) { a.Add("--size"); a.Add($"{sz.W},{sz.H}"); }
        if (f.FNumber is float fn) { a.Add("--f-number"); a.Add(fn.ToString("R", inv)); if (f.FocusMm is float z) { a.Add("--focus-depth"); a.Add(z.ToString("R", inv)); } }
        if (!Spec.UseSavedEdits) a.Add("--no-edits");
        if (Spec.Transform is { } t)
        {
            static string B(float v) => "0x" + BitConverter.SingleToInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture);
            a.Add("--transform"); a.Add(string.Join(',', t.Matrix.Select(B)) + ";" + string.Join(',', new[] { t.CropX0, t.CropY0, t.CropX1, t.CropY1 }.Select(B)));
        }
        a.Add("--formats"); a.Add("jpg");
        return string.Join(' ', a);
    }

    /// <summary>Render distinct frame <paramref name="k"/> (RGBA8, <see cref="AnimationPlan.Size"/>).</summary>
    public Rgba Render(int k, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var f = Plan.Frames[k];
        switch (Spec.Effect)
        {
            case AnimationEffect.RackFocus:
            case AnimationEffect.ApertureSweep:
            {
                var req = BaseRequest(Spec, Plan.Level, Plan.RenderSize, true) with { DepthEffectFNumber = f.FNumber, FocusDepthMm = f.FNumber is null ? null : f.FocusMm };
                var es = Session.PrepareExportSession(req, cancel);
                var (_, px) = es.Rgba8();
                return new Rgba(es.Grid.Size.W, es.Grid.Size.H, px);
            }
            case AnimationEffect.ParallaxSweep:
            {
                var (src, donors, pts, cz, inpaint) = ParallaxInputs(cancel);
                cancel.ThrowIfCancellationRequested();
                var (v, rep) = ParallaxRender.Frame(src, pts[k].X, pts[k].Y, cz, donors, inpaint);
                _log?.Invoke($"frame {k + 1}/{pts.Count} {rep}");
                return TurnRgba(v.Colour, Spec.TurnDegrees);
            }
            default:
                return TurnRgba(LensFrames(cancel)[k], Spec.TurnDegrees);
        }
    }

    // ---- the parallax sweep: the source (the export's JPEG render at the level + its depth on the grid, scaled), the donors, the path
    (ParallaxSource Src, IReadOnlyList<Donor> Donors, List<(double X, double Y)> Pts, double Cz, bool Inpaint)? _px;

    (ParallaxSource, IReadOnlyList<Donor>, List<(double X, double Y)>, double, bool) ParallaxInputs(CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_px is { } hit) return hit;
            var P = ParallaxRequestOf(Spec, Spec.LongEdge);
            var req = BaseRequest(Spec, Plan.Level, null, true) with { Parallax = P };
            var es = Session.PrepareExportSession(req, cancel);
            var src = es.ParallaxSource().Scaled(P.Size);
            var lri = Session.Lri;
            var f = P.Formats[0];
            IReadOnlyList<Donor> donors = Array.Empty<Donor>();
            var group = Wigglegram.SelectModules(lri, 0, includeMono: false);
            if (f != ParallaxFormat.Single && group.Count >= 2)
            {
                // the donors exactly as `convert` builds them: every colour wide module but the reference, rendered at native size
                var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
                ulong expRef = ModuleRender.ExposureReference(lri, lri.ReferenceModule);
                var native = new Dictionary<string, Rgba>();
                foreach (var name in group)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (name == lri.ReferenceModule) continue;
                    var img = ModuleRender.Render(lri, colour, wb, name, lri.Modules[name], expRef, 0.95f, 0, RendererProfile.Desktop);
                    native[name] = ModuleRender.ToRgba(img);
                }
                donors = Fill.Build(src, lri, es.Registration(), native, _log);
            }
            var kind = f == ParallaxFormat.Orbit ? Paths.Kind.Orbit : Paths.Parse(P.Path);
            double cz = ParallaxRender.Converge(P, src, _log);
            var pts = Paths.Generate(kind, P.Frames, P.Baseline, Paths.Axis(src.Geometry));
            _px = (src, donors, pts, cz, P.Fill != "none");
            return _px.Value;
        }
    }

    // ---- the lenses' own pictures (wigglegram: colour-matched wide lenses; lens tour: every lens)
    List<Rgba>? _lens;

    List<Rgba> LensFrames(CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_lens is not null) return _lens;
            var lri = Session.Lri;
            var colour = LumenProfile.Compute(lri); var wb = WhiteBalance.CaptureWb.From(lri, colour);
            bool wiggle = Spec.Effect == AnimationEffect.Wigglegram;
            ulong expRef = ModuleRender.ExposureReference(lri, wiggle ? lri.ReferenceModule : "A1");
            // each lens rendered on its own (a module render is a pure function of the capture: the same pixels in any order); two at once
            // (a full-resolution module render peaks near 2 GB; four at once need about 8 GB)
            var made = new Wigglegram.Frame[Plan.Frames.Count];
            Parallel.For(0, made.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Session.Options.Threads / 8, 1, 2), CancellationToken = cancel }, i =>
            {
                var fi = Plan.Frames[i];
                var img = ModuleRender.Render(lri, colour, wb, fi.Module!, lri.Modules[fi.Module!], expRef, 0.95f, Spec.LensIspLevel, RendererProfile.Desktop);
                made[i] = ModuleRender.ToFrame(img, Spec.LongEdge);
            });
            var frames = made.ToList();
            if (wiggle) Wigglegram.MatchColour(frames);
            int w = frames.Max(x => x.Width), h = frames.Max(x => x.Height);
            _lens = frames.Select(x => Fit(ToRgba(x), w, h)).ToList();
            return _lens;
        }
    }

    /// <summary>A frame turned clockwise by 90, 180 or 270 degrees (a pixel permutation); 0 = the frame itself.</summary>
    public static Rgba TurnRgba(Rgba img, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return img;
        int w = img.W, h = img.H;
        var o = degrees == 180 ? new Rgba(w, h) : new Rgba(h, w);
        var src = img.P; var dst = o.P;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx, dy, ow;
                switch (degrees)
                {
                    case 90: dx = h - 1 - y; dy = x; ow = h; break;
                    case 180: dx = w - 1 - x; dy = h - 1 - y; ow = w; break;
                    default: dx = y; dy = w - 1 - x; ow = h; break;
                }
                Buffer.BlockCopy(src, (y * w + x) * 4, dst, (dy * ow + dx) * 4, 4);
            }
        return o;
    }

    static Rgba ToRgba(Wigglegram.Frame f)
    {
        var o = new Rgba(f.Width, f.Height);
        for (long i = 0, p = 0, q = 0; i < (long)f.Width * f.Height; i++, p += 4, q += 3)
        { o.P[p] = f.Rgb[q]; o.P[p + 1] = f.Rgb[q + 1]; o.P[p + 2] = f.Rgb[q + 2]; o.P[p + 3] = 255; }
        return o;
    }

    /// <summary>Centre a smaller frame on a black frame of the animation's size (lenses of one capture normally share a size).</summary>
    static Rgba Fit(Rgba img, int w, int h)
    {
        if (img.W == w && img.H == h) return img;
        var o = new Rgba(w, h);
        for (long p = 3; p < o.P.LongLength; p += 4) o.P[p] = 255;
        int ox = (w - img.W) / 2, oy = (h - img.H) / 2;
        for (int y = 0; y < img.H; y++) Buffer.BlockCopy(img.P, y * img.W * 4, o.P, ((oy + y) * w + ox) * 4, img.W * 4);
        return o;
    }
}
