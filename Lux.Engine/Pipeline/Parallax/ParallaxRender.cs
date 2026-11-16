using System.Globalization;
using Lux.Engine.Session;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>The depth-based parallax renders, each a function of the in-memory <see cref="ParallaxSource"/>, the
/// request and (where the format wants them) the donors. The CLI (`lux-light`) and the desktop app share this code, so their
/// animations render the same frames.</summary>
public static class ParallaxRender
{
    /// <summary>Resolve the convergence plane in millimetres: `--parallax-converge-at x,y` reads the depth map at a
    /// point (the exact analytic replacement for the wigglegram's phase-correlation pivot), `auto` is the median
    /// depth (the pivot in the middle of the scene), a number is metres, `none` is 0.</summary>
    public static double Converge(ParallaxRequest r, ParallaxSource src, Action<string>? log)
    {
        if (r.ConvergeAt is { } at)
        {
            int x = Math.Clamp(at.X, 0, src.W - 1), y = Math.Clamp(at.Y, 0, src.H - 1);
            double z = Dibr.DepthAt(src.Depth, x, y);
            log?.Invoke($"convergence read from the depth map at ({x},{y}): {z / 1000:F3} m");
            return z;
        }
        string c = r.Converge.ToLowerInvariant();
        if (c is "auto") { double z = src.Depth.Percentile(0.5); log?.Invoke($"convergence auto = median depth {z / 1000:F3} m"); return z; }
        if (c is "none" or "off") return 0;
        double m = double.Parse(c, CultureInfo.InvariantCulture) * 1000;
        log?.Invoke($"convergence plane {m / 1000:F3} m");
        return m;
    }

    /// <summary>Synthesise one virtual view and fill it. Returns the view plus a one-line account of where the
    /// disocclusion pixels came from.</summary>
    public static (View V, string Report) Frame(ParallaxSource src, double tx, double ty, double convergeZ,
                                                IReadOnlyList<Donor> donors, bool inpaint)
    {
        var v = Dibr.Synthesise(src, tx, ty, convergeZ);
        int before = v.HoleCount;
        var used = Fill.FromDonors(v, donors, src.Geometry.FocalPx, convergeZ, src.W, src.H);
        int afterDonors = v.HoleCount;
        int painted = inpaint ? Fill.Inpaint(v, src.W, src.H) : 0;
        string rep = $"holes {before} ({100.0 * before / ((long)src.W * src.H):F2}%)"
                   + (used.Count > 0 ? $" — from real modules: {string.Join(", ", used.Select(kv => $"{kv.Key} {kv.Value}"))}" : "")
                   + (painted > 0 ? $" — inpainted {painted}" : "")
                   + (afterDonors > 0 && !inpaint ? $" — {afterDonors} left empty" : "");
        return (v, rep);
    }

    static double PointDepth(ParallaxSource src, (int X, int Y) p) =>
        Dibr.DepthAt(src.Depth, Math.Clamp(p.X, 0, src.W - 1), Math.Clamp(p.Y, 0, src.H - 1));

    /// <summary>`parallax-wiggle-interp` (sweep/arc/line), `parallax-orbit` and `parallax-single`: N virtual
    /// viewpoints along a path. Returns the unique frames; <paramref name="closed"/> says whether the path loops.</summary>
    public static List<Rgba> Sweep(ParallaxSource src, ParallaxRequest r, ParallaxFormat f, IReadOnlyList<Donor> donors,
                                   Action<string>? log, out bool closed)
    {
        var kind = f == ParallaxFormat.Orbit ? Paths.Kind.Orbit : Paths.Parse(r.Path);
        closed = Paths.Closed(kind);
        int n = r.Frames;
        double cz = Converge(r, src, log);
        bool inpaint = r.Fill != "none";
        var axis = Paths.Axis(src.Geometry);
        var pts = Paths.Generate(kind, n, r.Baseline, axis);
        log?.Invoke($"{ExportFormats.NameOf(f)}: {n} frames, path {kind}, baseline {r.Baseline:F2} mm along ({axis.X:F3},{axis.Y:F3})"
                  + $" [{WineCrt.Atan2(axis.Y, axis.X) * 180 / Math.PI:F1}° off horizontal], f = {src.Geometry.FocalPx:F1} px"
                  + (f == ParallaxFormat.Single ? ", inpainting only" : ""));
        var frames = new List<Rgba>(n);
        for (int i = 0; i < pts.Count; i++)
        {
            var (v, rep) = Frame(src, pts[i].X, pts[i].Y, cz, donors, inpaint);
            frames.Add(v.Colour);
            log?.Invoke($"frame {i + 1,2}/{pts.Count} t=({pts[i].X,7:F2},{pts[i].Y,7:F2}) mm  {rep}");
        }
        return frames;
    }

    /// <summary>Ping-pong unless `--parallax-loop forward`; a closed path plays forward by default.</summary>
    public static List<Rgba> Played(IReadOnlyList<Rgba> frames, ParallaxRequest r, bool closed)
    {
        bool ping = (r.Loop ?? (closed ? "forward" : "pingpong")) != "forward";
        return ping ? Boomerang(frames) : frames.ToList();
    }

    static double FocusOf(ParallaxSource src, (int X, int Y)? point, double? metres, double dflt) =>
        point is { } p ? PointDepth(src, p) : metres is { } m ? m * 1000 : dflt;

    /// <summary>`parallax-dof`: the layered depth-of-field composite, one still.</summary>
    public static Rgba Dof(ParallaxSource src, ParallaxRequest r, Action<string>? log)
    {
        double focus = FocusOf(src, r.FocusAt, r.FocusM, src.Depth.Percentile(0.1));
        double f = src.Geometry.FocalPx;
        log?.Invoke($"parallax-dof: focus {focus / 1000:F3} m, aperture {r.Aperture:F1} mm ({r.Layers} layers); circle of confusion "
                  + $"{Effects.Coc(src.Depth.Percentile(0.02), focus, r.Aperture, f):F1} px at the 2nd-percentile depth, "
                  + $"{Effects.Coc(src.Depth.Percentile(0.98), focus, r.Aperture, f):F1} px at the 98th");
        return Effects.DepthOfField(src.Colour, src.Depth, f, focus, r.Aperture, r.Layers, 64);
    }

    /// <summary>`parallax-rack`: the focus pulled from one depth to another. Interpolated in 1/Z — focus pulls are
    /// linear in the lens's own travel, not in metres, and a linear ramp in Z spends most of the animation where
    /// nothing visibly changes — with a cosine ease. Default pull: the 10th- to the 90th-percentile depth.</summary>
    public static List<Rgba> Rack(ParallaxSource src, ParallaxRequest r, Action<string>? log)
    {
        double z1, z2;
        if (r.RackAt is { } ra) { z1 = PointDepth(src, ra.A); z2 = PointDepth(src, ra.B); }
        else if (r.RackM is { } rm) { z1 = rm.M1 * 1000; z2 = rm.M2 * 1000; }
        else { z1 = src.Depth.Percentile(0.1); z2 = src.Depth.Percentile(0.9); }
        int n = r.Frames;
        log?.Invoke($"parallax-rack: {z1 / 1000:F3} m -> {z2 / 1000:F3} m, aperture {r.Aperture:F1} mm, {n} frames, {r.Layers} layers");
        var frames = new List<Rgba>(n);
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (double)i / (n - 1);
            double s = 0.5 - 0.5 * WineCrt.Cos(Math.PI * t);       // ease in and out
            double z = 1.0 / ((1 - s) / z1 + s / z2);
            frames.Add(Effects.DepthOfField(src.Colour, src.Depth, src.Geometry.FocalPx, z, r.Aperture, r.Layers, 64));
            log?.Invoke($"frame {i + 1,2}/{n} focus {z / 1000:F3} m");
        }
        return frames;
    }

    /// <summary>`parallax-dolly`: translate in Z while scaling the FOV to hold the subject. The travel runs
    /// 0 → −dz: the camera pulls BACK while the focal length grows, the one direction a single capture can carry
    /// (every magnification is then ≥ 1, the frame stays full, nothing near diverges). Dollying IN is allowed but
    /// clamped, and the clamp is visible.</summary>
    public static List<Rgba> Dolly(ParallaxSource src, ParallaxRequest r, Action<string>? log)
    {
        double subject = r.SubjectAt is { } sa ? PointDepth(src, sa) : (r.SubjectM ?? src.Depth.Percentile(0.1) / 1000) * 1000;
        double dz = r.Dz;
        int n = r.Frames;
        double zLimit = src.Depth.Percentile(0.01);
        if (dz < 0 && -dz > 0.4 * zLimit)
        {
            log?.Invoke($"note: dollying IN is clamped to 40% of the 1st-percentile depth ({0.4 * zLimit:F0} mm);"
                      + " nearer than that the magnification diverges and the frame edges have no data at all");
            dz = -0.4 * zLimit;
        }
        log?.Invoke($"parallax-dolly: subject at {subject / 1000:F3} m, travel {Math.Abs(dz):F0} mm"
                  + $" ({(dz >= 0 ? "back, zooming in" : "in, zooming out")}), {n} frames");
        var frames = new List<Rgba>(n);
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (double)i / (n - 1);
            double s = 0.5 - 0.5 * WineCrt.Cos(Math.PI * t);     // eased 0 -> 1
            double d = -dz * s;
            var v = Dibr.DollyZoom(src.Colour, src.Depth, d, subject);
            if (v.HoleCount > 0) Fill.Inpaint(v, src.W, src.H);
            frames.Add(v.Colour);
            log?.Invoke($"frame {i + 1,2}/{n} dz {d,7:F1} mm  zoom {(subject - d) / subject:F4}  holes {v.HolePercent(src.W, src.H):F2}%");
        }
        return frames;
    }

    /// <summary>The stereo pair behind `parallax-anaglyph` / `parallax-crosseye` / `parallax-sbs`: two views ±ipd/2
    /// along the horizontal (a stereo pair must not carry vertical disparity), converged so the subject sits at the
    /// screen plane. One pair serves every presentation requested.</summary>
    public static (Rgba Left, Rgba Right) StereoPair(ParallaxSource src, ParallaxRequest r, double ipd, IReadOnlyList<Donor> donors, Action<string>? log)
    {
        double cz = Converge(r, src, log);
        if (cz <= 0) { cz = src.Depth.Percentile(0.35); log?.Invoke($"no convergence given; using the 35th-percentile depth {cz / 1000:F3} m so the subject sits at the screen plane"); }
        double f = src.Geometry.FocalPx;
        log?.Invoke($"stereo: ipd {ipd:F1} mm, f = {f:F1} px, max disparity {f * ipd * Math.Abs(1.0 / src.Depth.Percentile(0.02) - 1.0 / cz):F1} px at the 2nd percentile depth");
        var (lv, lrep) = Frame(src, -ipd / 2, 0, cz, donors, true);
        var (rv, rrep) = Frame(src, ipd / 2, 0, cz, donors, true);
        log?.Invoke($"left  {lrep}");
        log?.Invoke($"right {rrep}");
        return (lv.Colour, rv.Colour);
    }

    /// <summary>`parallax-still`: one synthesised viewpoint at `--parallax-t tx,ty` mm.</summary>
    public static Rgba Still(ParallaxSource src, ParallaxRequest r, IReadOnlyList<Donor> donors, Action<string>? log)
    {
        double cz = Converge(r, src, log);
        var (v, rep) = Frame(src, r.T.X, r.T.Y, cz, donors, r.Fill != "none");
        log?.Invoke($"parallax-still: t = ({r.T.X:F2},{r.T.Y:F2}) mm  {rep}");
        return v.Colour;
    }

    /// <summary>Ping-pong: play out and back, minus the repeated endpoints. A straight loop snaps across the whole
    /// baseline in one frame, which is the artefact a sweep order exists to avoid.</summary>
    public static List<Rgba> Boomerang(IReadOnlyList<Rgba> f)
    {
        if (f.Count < 3) return f.ToList();
        var o = new List<Rgba>(f);
        for (int i = f.Count - 2; i >= 1; i--) o.Add(f[i]);
        return o;
    }
}
