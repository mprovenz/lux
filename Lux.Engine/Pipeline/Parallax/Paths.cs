namespace Lux.Engine.Pipeline.Parallax;

/// <summary>
/// Virtual camera paths.
///
/// The physical wigglegram (`parallax-wiggle`) plays the four A modules in spatial order, which gives four samples of
/// an 80–100 px disparity range: ~25 px steps, and the stepping is visible. Here the path is continuous and the sample
/// count is free, so the same swing can be played in as many frames as the container will carry.
///
/// The **axis** is not invented either. The A modules are bolted to the body, so their layout is a property of the
/// camera; the dominant eigenvector of the colour modules' centres is the direction the rig is actually spread along
/// (~12° off horizontal on this corpus), and a sweep along it is the widest baseline the camera can offer.
/// </summary>
public static class Paths
{
    public enum Kind { Sweep, Orbit, Arc, Line }

    public static Kind Parse(string? s) => (s ?? "sweep").ToLowerInvariant() switch
    {
        "sweep" => Kind.Sweep,
        "orbit" or "circle" => Kind.Orbit,
        "arc" => Kind.Arc,
        "line" or "horizontal" => Kind.Line,
        _ => throw new ArgumentException($"unknown path '{s}' (sweep, orbit, arc, line)"),
    };

    /// <summary>The rig's dominant axis, as a unit vector in image axes, from the module centres.</summary>
    public static (double X, double Y) Axis(ParallaxGeometry g)
    {
        // Only the reference module's own colour group defines the rig axis. A B-reference capture usually has
        // canonical extrinsics for the reference alone (the movable-mirror B/C modules carry none), and there is then
        // no measured axis at all — horizontal is the honest default, not a PCA over a different group's modules.
        if (g.PathModules.Count < 3) return (1, 0);
        var pts = g.PathModules.Where(g.Centres.ContainsKey).Select(n => g.Centres[n]).Select(c => (X: c.X, Y: c.Y)).ToList();
        if (pts.Count < 3) return (1, 0);
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts) { double ddx = p.X - mx, ddy = p.Y - my; sxx += ddx * ddx; syy += ddy * ddy; sxy += ddx * ddy; }
        double tr = sxx + syy, det = sxx * syy - sxy * sxy;
        double disc = Math.Sqrt(Math.Max(tr * tr - 4 * det, 0));
        double lam = (tr + disc) / 2;
        double vx, vy;
        if (Math.Abs(sxy) > 1e-9) { vx = sxy; vy = lam - sxx; }
        else { vx = sxx >= syy ? 1 : 0; vy = sxx >= syy ? 0 : 1; }
        double len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1e-12) return (1, 0);
        vx /= len; vy /= len;
        if (vx < 0) { vx = -vx; vy = -vy; }   // keep the sweep left-to-right so a ping-pong reads the same way every time
        return (vx, vy);
    }

    /// <summary>The camera positions of a path, in millimetres, centred on the reference module.</summary>
    /// <param name="baselineMm">peak-to-peak extent of the path (its diameter, for an orbit)</param>
    public static List<(double X, double Y)> Generate(Kind kind, int n, double baselineMm, (double X, double Y) axis, double arcRise = 0.25)
    {
        var pts = new List<(double, double)>(n);
        double r = baselineMm / 2;
        double px = -axis.Y, py = axis.X;      // perpendicular to the rig axis
        switch (kind)
        {
            case Kind.Sweep:
            case Kind.Line:
            {
                var a = kind == Kind.Line ? (X: 1.0, Y: 0.0) : axis;
                for (int i = 0; i < n; i++)
                {
                    // cosine-eased so the ends of a ping-pong do not snap; the middle moves fastest, as a real swing does
                    double t = n == 1 ? 0 : (double)i / (n - 1);
                    double s = -Math.Cos(Math.PI * t);
                    pts.Add((a.X * r * s, a.Y * r * s));
                }
                break;
            }
            case Kind.Orbit:
                for (int i = 0; i < n; i++)
                {
                    double th = 2 * Math.PI * i / n;   // closed loop: no ping-pong needed, and none of its endpoint stall
                    double c = Math.Cos(th), s = Math.Sin(th);
                    pts.Add((axis.X * r * c + px * r * s, axis.Y * r * c + py * r * s));
                }
                break;
            case Kind.Arc:
                for (int i = 0; i < n; i++)
                {
                    double t = n == 1 ? 0 : (double)i / (n - 1);
                    double s = -Math.Cos(Math.PI * t);
                    double rise = arcRise * r * (1 - s * s);        // bulges perpendicular at the middle of the sweep
                    pts.Add((axis.X * r * s + px * rise, axis.Y * r * s + py * rise));
                }
                break;
        }
        return pts;
    }

    /// <summary>Whether a path closes on itself (an orbit) or has to be played out and back.</summary>
    public static bool Closed(Kind k) => k == Kind.Orbit;
}
