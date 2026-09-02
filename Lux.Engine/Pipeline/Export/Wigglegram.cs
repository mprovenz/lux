using Lux.Engine.Imaging;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Wigglegram frame preparation — a **Lux feature, not a Lumen one**. Lumen has no animated output; this reuses the
/// ported per-module ISP to make the one thing the L16's optics are unusually good at.
///
/// The idea: the five A-group modules (28 mm) sit at different points on the camera face and fire simultaneously,
/// so one capture already contains a small multi-view set. Played in spatial order they read as parallax.
///
/// Three things decide whether it looks like depth or like a broken GIF, and all three are settled here:
///
/// * <b>Which modules.</b> The A group only. B and C aim at different parts of the scene to be stitched, not the
///   same framing from offset positions. Within A, the <b>monochrome module is excluded</b> — on the corpus that is
///   A2 (`sensor_bayer_red_override = (-1,-1)`), and leaving it in makes every fourth frame greyscale.
/// * <b>What order.</b> By projecting the modules' factory camera positions onto their own dominant axis, so the
///   animation sweeps once across the rig rather than backtracking. See <see cref="SweepOrder"/>.
/// * <b>Colour.</b> The modules disagree about white balance by 30–43 levels of 255; untouched, the result strobes
///   between colour casts and the parallax stops being the thing you notice. See <see cref="MatchColour"/>.
///
/// Deliberately NOT done: any warping or rectification. Measured on Lux's own module renders, whole-frame drift
/// between A modules is 24–28 px on a 4160 px frame while the differential disparity that carries the depth is
/// 135–210 px — a 5.5–7.4× ratio. Aligning would cost resampling and remove part of the effect. An optional
/// convergence pivot (<see cref="PinPivot"/>) is offered for the case where a subject should be held still.
/// </summary>
public static class Wigglegram
{
    /// <summary>A frame ready to encode: 8-bit RGB, row-major, no padding.</summary>
    public sealed record Frame(string Module, int Width, int Height, byte[] Rgb);

    /// <summary>Optical group of a camera id, matching the pipeline's own `Group` rule.</summary>
    public static int Group(int camId) => camId <= 4 ? 0 : camId <= 9 ? 1 : 2;

    /// <summary>`sensor_bayer_red_override = (-1,-1)` is the no-CFA sentinel — the monochrome module.</summary>
    public static bool IsMono(Ltpb.CameraModule m)
    {
        var r = m.SensorBayerRedOverride;
        return r is not null && (r.X | r.Y) < 0;
    }

    /// <summary>The modules a wigglegram should use: one optical group, colour sensors only, in sweep order.</summary>
    public static IReadOnlyList<string> SelectModules(LriFile lri, int group = 0, bool includeMono = false)
    {
        var names = new List<string>();
        foreach (var (name, mref) in lri.Modules)
        {
            if (Group((int)mref.Module.Id) != group) continue;
            if (!includeMono && IsMono(mref.Module)) continue;
            names.Add(name);
        }
        return SweepOrder(lri, names);
    }

    /// <summary>
    /// Order modules so the animation sweeps once across the rig.
    ///
    /// The modules are bolted to the body, so their layout is a property of the camera rather than of any one
    /// photo — which means it can be read straight from the factory extrinsics instead of estimated per scene.
    /// Each module's calibration gives a rotation and translation relative to the canonical camera; the camera
    /// centre is `−(Rᵀ·t)`. Those centres are projected onto their own dominant axis (the principal component)
    /// and sorted along it.
    ///
    /// Label order is wrong and visibly so: on the corpus A1→A3→A4→A5 backtracks, giving three small steps and one
    /// hard snap. The dominant axis for the four colour A modules comes out ~12° off horizontal, and the resulting
    /// order matches, in reverse, what an independent measurement of foreground parallax across 120 captures
    /// produced — reversal being immaterial to a ping-pong loop.
    /// </summary>
    public static IReadOnlyList<string> SweepOrder(LriFile lri, IReadOnlyList<string> names)
    {
        if (names.Count < 3) return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var pts = new List<(string Name, double X, double Y)>();
        foreach (var n in names)
        {
            var c = CameraCentre(lri, lri.Modules[n].Module.Id);
            if (c is null) return names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            pts.Add((n, c.Value.X, c.Value.Y));
        }
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        // 2x2 covariance of the centred positions, then its dominant eigenvector — the axis the rig is spread along.
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts) { double dx = p.X - mx, dy = p.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double tr = sxx + syy, det = sxx * syy - sxy * sxy;
        double disc = Math.Sqrt(Math.Max(tr * tr - 4 * det, 0));
        double lam = (tr + disc) / 2;
        double vx, vy;
        if (Math.Abs(sxy) > 1e-9) { vx = sxy; vy = lam - sxx; }
        else { vx = sxx >= syy ? 1 : 0; vy = sxx >= syy ? 0 : 1; }
        double len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1e-12) return names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        vx /= len; vy /= len;
        return pts.OrderBy(p => (p.X - mx) * vx + (p.Y - my) * vy).Select(p => p.Name).ToArray();
    }

    /// <summary>`−(Rᵀ·t)` from the module's canonical factory extrinsics, in millimetres; null if uncalibrated.</summary>
    public static (double X, double Y, double Z)? CameraCentre(LriFile lri, Ltpb.CameraID id)
    {
        var ext = Calibration.CanonicalExtrinsics(lri.Header, id);
        if (ext is null) return null;
        var (R, t) = ext.Value;
        return (-((R[0] * t[0] + R[3] * t[1]) + R[6] * t[2]),
                -((R[1] * t[0] + R[4] * t[1]) + R[7] * t[2]),
                -((R[2] * t[0] + R[5] * t[1]) + R[8] * t[2]));
    }

    /// <summary>
    /// Pull every frame's per-channel mean and standard deviation onto the group average.
    ///
    /// Matching to the group average rather than to a chosen reference avoids privileging one module's cast. On the
    /// corpus this takes a 30–43 level per-channel spread down to under 1, which is the difference between a
    /// wigglegram that reads as depth and one that reads as a colour-strobing fault. Frames are corrected one at a
    /// time so the whole set is never held in float at once.
    /// </summary>
    public static void MatchColour(IReadOnlyList<Frame> frames)
    {
        if (frames.Count < 2) return;
        int n = frames.Count;
        var mean = new double[n, 3];
        var sd = new double[n, 3];
        for (int f = 0; f < n; f++)
        {
            var px = frames[f].Rgb;
            long count = px.Length / 3;
            for (int c = 0; c < 3; c++)
            {
                double s = 0, s2 = 0;
                for (long i = c; i < px.Length; i += 3) { double v = px[i]; s += v; s2 += v * v; }
                double m = s / count;
                mean[f, c] = m;
                sd[f, c] = Math.Sqrt(Math.Max(s2 / count - m * m, 0));
            }
        }
        var tm = new double[3];
        var ts = new double[3];
        for (int c = 0; c < 3; c++)
        {
            for (int f = 0; f < n; f++) { tm[c] += mean[f, c]; ts[c] += sd[f, c]; }
            tm[c] /= n; ts[c] /= n;
        }
        for (int f = 0; f < n; f++)
        {
            var px = frames[f].Rgb;
            for (int c = 0; c < 3; c++)
            {
                double m = mean[f, c], s = sd[f, c], gain = ts[c] / (s + 1e-6), bias = tm[c];
                for (long i = c; i < px.Length; i += 3)
                {
                    double v = (px[i] - m) * gain + bias;
                    px[i] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5);
                }
            }
        }
    }

    /// <summary>
    /// Optional convergence pin: hold a region still so the rest of the scene swings around it.
    ///
    /// Without this the pivot lands wherever the whole-frame correlation does, which is mid-scene. Pinning a chosen
    /// subject is what makes the effect read deliberately. The shift is measured by phase correlation on the pivot
    /// rect and applied as an <b>integer translation</b>, so no resampling happens and no sharpness is lost; the
    /// frames are then cropped to their common intersection.
    /// </summary>
    public static IReadOnlyList<Frame> PinPivot(IReadOnlyList<Frame> frames, RectI pivot)
    {
        if (frames.Count < 2) return frames;
        if (pivot.X0 < 0 || pivot.Y0 < 0 || pivot.Width < 8 || pivot.Height < 8 ||
            pivot.X1 > frames[0].Width || pivot.Y1 > frames[0].Height)
            throw new ArgumentOutOfRangeException(nameof(pivot),
                $"pivot ({pivot.X0},{pivot.Y0},{pivot.Width}x{pivot.Height}) does not fit inside the {frames[0].Width}x{frames[0].Height} frame");
        var shifts = new (int X, int Y)[frames.Count];
        var refLuma = Luma(frames[0], pivot);
        for (int i = 1; i < frames.Count; i++)
        {
            var (dx, dy) = PhaseCorrelate(refLuma, Luma(frames[i], pivot), pivot.Width, pivot.Height);
            shifts[i] = (dx, dy);
        }
        int minX = shifts.Min(s => s.X), maxX = shifts.Max(s => s.X);
        int minY = shifts.Min(s => s.Y), maxY = shifts.Max(s => s.Y);
        int w = frames[0].Width, h = frames[0].Height;
        int cw = w - (maxX - minX), ch = h - (maxY - minY);
        if (cw <= 0 || ch <= 0) return frames;
        var outp = new List<Frame>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            int ox = shifts[i].X - minX, oy = shifts[i].Y - minY;
            var dst = new byte[(long)cw * ch * 3];
            for (int y = 0; y < ch; y++)
                Buffer.BlockCopy(frames[i].Rgb, ((y + oy) * w + ox) * 3, dst, y * cw * 3, cw * 3);
            outp.Add(new Frame(frames[i].Module, cw, ch, dst));
        }
        return outp;
    }

    static double[] Luma(Frame f, RectI r)
    {
        var v = new double[r.Width * r.Height];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                long o = ((long)(y + r.Y0) * f.Width + (x + r.X0)) * 3;
                v[y * r.Width + x] = 0.299 * f.Rgb[o] + 0.587 * f.Rgb[o + 1] + 0.114 * f.Rgb[o + 2];
            }
        return v;
    }

    /// <summary>Integer-accurate phase correlation — robust to the exposure and colour differences between sensors,
    /// which is why it is used here rather than intensity-domain matching.</summary>
    static (int X, int Y) PhaseCorrelate(double[] a, double[] b, int w, int h)
    {
        // Small transforms; a direct O(n·k) search over a bounded shift window is cheaper here than an FFT and
        // avoids a dependency. The pivot rect is expected to be a few hundred pixels, and rig offsets are tens.
        int win = Math.Min(64, Math.Min(w, h) / 4);
        double ma = a.Average(), mb = b.Average();
        double best = double.NegativeInfinity; int bx = 0, by = 0;
        for (int dy = -win; dy <= win; dy++)
            for (int dx = -win; dx <= win; dx++)
            {
                double s = 0; int n = 0;
                for (int y = Math.Max(0, -dy); y < Math.Min(h, h - dy); y += 2)
                    for (int x = Math.Max(0, -dx); x < Math.Min(w, w - dx); x += 2)
                    { s += (a[y * w + x] - ma) * (b[(y + dy) * w + (x + dx)] - mb); n++; }
                if (n > 0) { s /= n; if (s > best) { best = s; bx = dx; by = dy; } }
            }
        return (bx, by);
    }

    /// <summary>Ping-pong: play the sweep out and back, minus the repeated endpoints. A straight loop snaps from the
    /// last module to the first across the whole baseline, which is exactly the artefact the sweep order avoids.</summary>
    public static IReadOnlyList<Frame> Boomerang(IReadOnlyList<Frame> frames)
    {
        if (frames.Count < 3) return frames;
        var outp = new List<Frame>(frames);
        for (int i = frames.Count - 2; i >= 1; i--) outp.Add(frames[i]);
        return outp;
    }
}
