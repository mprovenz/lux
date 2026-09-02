using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>A real module standing by to fill in what the reference view cannot see.</summary>
public sealed class Donor
{
    public required string Name;
    public required Rgba Colour;      // the module frame on the export grid, colour-matched to the reference
    public required Plane Z;          // depth at that module's viewpoint, disocclusions filled with background depth
    public required double Bx, By;    // its baseline from the reference, mm, in image axes
}

/// <summary>
/// Disocclusion filling.
///
/// This is where the L16 has something a single-image "3D photo" tool does not: **four genuine viewpoints**. When the
/// virtual camera moves left, the strip of background that appears from behind a foreground object was never seen by
/// the reference module — but it very likely *was* seen by A5, which is 43.5 mm to the left and photographed it at the
/// same instant. Filling from a real photograph beats inventing texture, and it is checkable.
///
/// Two stages, in order:
/// 1. <see cref="FromDonors"/> — warp each real module into the virtual camera and take its pixels where the
///    reference-based synthesis has none. Modules are tried nearest-first, so the least additional warping wins.
/// 2. <see cref="Inpaint"/> — whatever is still empty is filled from the surrounding pixels, biased towards the
///    **farther** side. A disocclusion is by definition background revealed from behind a foreground edge, so pulling
///    the foreground colour into it is the one thing guaranteed to be wrong; that is what produces the halo that
///    single-image 3D photos are known for.
///
/// Honest measurement (spec `a-parallax-experiments.md` §5): the donor fill is not demonstrated to beat plain
/// inpainting — leave-one-out over nine module×capture cases averages −0.5 dB — and over a physical-baseline sweep
/// the two differ on under one percent of pixels. It stays the default because it puts photographed pixels where
/// an inpainter would guess; `inpaint` is a defensible choice and is ~12 s faster per capture.
/// </summary>
public static class Fill
{
    /// <summary>Build the donor set for a capture: every colour module of the reference's own group except the
    /// reference, from module frames already rendered at native resolution (<paramref name="native"/>, keyed by
    /// module name — the same renders the wigglegram uses), brought onto the export grid through the pipeline's own
    /// <c>AlignedCalib</c> and given the depth of its own viewpoint.</summary>
    public static List<Donor> Build(ParallaxSource src, LriFile lri, StereoAsyncApi api,
                                    IReadOnlyDictionary<string, Rgba> native, Action<string>? log)
    {
        string refName = lri.ReferenceModule;
        var placed = new List<(string, Rgba, double, double)>();
        foreach (var name in native.Keys.Where(n => n != refName).OrderBy(n => n, StringComparer.Ordinal))
        {
            var calib = ModuleGrid.CalibFor(lri, api, name);
            var onGrid = ModuleGrid.ToExportGrid(native[name], src.Geometry, calib, Affine.Identity);
            var (bx, by) = src.Geometry.BaselineOf(name, refName);
            placed.Add((name, onGrid, bx, by));
        }
        return BuildFrom(src, placed, log);
    }

    /// <summary>The donor construction proper, from module frames already on the export grid.</summary>
    public static List<Donor> BuildFrom(ParallaxSource src, IEnumerable<(string Name, Rgba OnGrid, double Bx, double By)> placed, Action<string>? log)
    {
        var donors = new List<Donor>();
        foreach (var (name, onGrid, bx, by) in placed)
        {
            var matched = ColourMatch(onGrid, src.Colour);
            // The depth at this module's viewpoint: the z-buffer of synthesising it from the reference. Its own holes
            // are exactly the places the reference could not see, so they are filled with the FARTHER neighbouring
            // depth — background, which is what a disocclusion reveals.
            var v = Dibr.Synthesise(src.Colour, src.Depth, src.Geometry.FocalPx, bx, by, 0);
            var z = BackgroundFill(v.Z, v.Hole, src.W, src.H);
            donors.Add(new Donor { Name = name, Colour = matched, Z = z, Bx = bx, By = by });
            log?.Invoke($"donor {name}: baseline ({bx:F2},{by:F2}) mm, {v.HolePercent(src.W, src.H):F2}% of its depth extrapolated");
        }
        return donors;
    }

    /// <summary>Clone a view so a fill strategy can be tried without disturbing another.</summary>
    public static View Clone(View v) => new()
    {
        Colour = v.Colour.Clone(), Z = (float[])v.Z.Clone(), Hole = (bool[])v.Hole.Clone(), Tx = v.Tx, Ty = v.Ty,
    };

    /// <summary>Pull <paramref name="a"/>'s per-channel mean and spread onto <paramref name="b"/>'s — the same
    /// correction the wigglegram applies between modules, so a module frame off the per-module ISP can sit next to
    /// the export image off the display ISP without a tonal step.</summary>
    public static Rgba ColourMatch(Rgba a, Rgba b)
    {
        var o = a.Clone();
        for (int c = 0; c < 3; c++)
        {
            double sa = 0, sa2 = 0, sb = 0, sb2 = 0; long n = 0;
            for (long i = c; i < a.P.LongLength; i += 4)
            { double va = a.P[i], vb = b.P[i]; sa += va; sa2 += va * va; sb += vb; sb2 += vb * vb; n++; }
            double ma = sa / n, sda = Math.Sqrt(Math.Max(sa2 / n - ma * ma, 0));
            double mb = sb / n, sdb = Math.Sqrt(Math.Max(sb2 / n - mb * mb, 0));
            double gain = sdb / (sda + 1e-6);
            for (long i = c; i < o.P.LongLength; i += 4)
                o.P[i] = (byte)Math.Clamp((a.P[i] - ma) * gain + mb + 0.5, 0, 255);
        }
        return o;
    }

    /// <summary>Replace the holes of a z-buffer with the farthest depth found by walking outwards along the four axes.</summary>
    public static Plane BackgroundFill(float[] z, bool[] hole, int w, int h)
    {
        var o = new Plane(w, h);
        Array.Copy(z, o.V, z.Length);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                if (!hole[i]) { if (float.IsInfinity(o.V[i])) o.V[i] = 0f; continue; }
                float best = 0f;
                for (int d = 0; d < 4; d++)
                {
                    int sx = d == 0 ? 1 : d == 1 ? -1 : 0, sy = d == 2 ? 1 : d == 3 ? -1 : 0;
                    int cx = x + sx, cy = y + sy;
                    for (int k = 0; k < 96 && cx >= 0 && cy >= 0 && cx < w && cy < h; k++, cx += sx, cy += sy)
                    {
                        long j = (long)cy * w + cx;
                        if (hole[j]) continue;
                        float v = z[j];
                        if (v > best && !float.IsInfinity(v)) best = v;
                        break;
                    }
                }
                o.V[i] = best;
            }
        });
        return o;
    }

    /// <summary>Fill a synthesised view's holes from the real modules. Returns the number of pixels each donor
    /// supplied, for reporting.</summary>
    public static Dictionary<string, int> FromDonors(View v, IReadOnlyList<Donor> donors, double focalPx, double convergeZmm, int w, int h)
    {
        var used = new Dictionary<string, int>();
        if (donors.Count == 0) return used;
        double invZ0 = convergeZmm > 0 && !double.IsInfinity(convergeZmm) ? 1.0 / convergeZmm : 0.0;
        // nearest donor first: the smaller the remaining translation, the less the donor has to be stretched
        foreach (var d in donors.OrderBy(d => (d.Bx - v.Tx) * (d.Bx - v.Tx) + (d.By - v.Ty) * (d.By - v.Ty)))
        {
            if (!v.Hole.Any(x => x)) break;
            // The donor's pixels already carry its own parallax, so only the difference is applied; the convergence
            // plane contributes a constant shift, which is why Synthesise takes an explicit offset.
            var dv = Dibr.Synthesise(d.Colour, d.Z, focalPx, v.Tx - d.Bx, v.Ty - d.By, 0,
                                     focalPx * v.Tx * invZ0, focalPx * v.Ty * invZ0);
            var corr = LowFrequencyCorrection(v, dv, w, h);
            int n = 0;
            for (long i = 0, p = 0; i < (long)w * h; i++, p += 4)
            {
                if (!v.Hole[i] || dv.Hole[i]) continue;
                for (int c = 0; c < 3; c++) v.Colour.P[p + c] = (byte)Math.Clamp(dv.Colour.P[p + c] + corr[i * 3 + c] + 0.5f, 0, 255);
                v.Colour.P[p + 3] = 255;
                v.Z[i] = dv.Z[i]; v.Hole[i] = false; n++;
            }
            if (n > 0) used[d.Name] = n;
        }
        return used;
    }

    /// <summary>
    /// Background-biased inpainting for whatever the donors could not reach.
    ///
    /// For every remaining hole pixel, gather the nearest filled pixel along eight rays and average them weighted by
    /// <c>Z²/dist</c>. The Z weight is the whole idea: a hole at a depth edge has foreground on one side and background
    /// on the other, and taking the background is right in every case where the hole exists *because* the camera
    /// moved. Averaging both sides equally is what smears a foreground silhouette outwards into a halo.
    /// </summary>
    public static int Inpaint(View v, int w, int h, int maxRadius = 128)
    {
        var holes = new List<long>();
        for (long i = 0; i < (long)w * h; i++) if (v.Hole[i]) holes.Add(i);
        if (holes.Count == 0) return 0;
        var src = v.Colour.Clone();
        var known = (bool[])v.Hole.Clone();
        int[] dxs = { 1, -1, 0, 0, 1, 1, -1, -1 }, dys = { 0, 0, 1, -1, 1, -1, 1, -1 };
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, holes.Count), range =>
        {
            for (int k = range.Item1; k < range.Item2; k++)
            {
                long i = holes[k];
                int x = (int)(i % w), y = (int)(i / w);
                double sr = 0, sg = 0, sb = 0, sw = 0;
                for (int d = 0; d < 8; d++)
                {
                    int cx = x + dxs[d], cy = y + dys[d];
                    for (int step = 1; step <= maxRadius; step++, cx += dxs[d], cy += dys[d])
                    {
                        if (cx < 0 || cy < 0 || cx >= w || cy >= h) break;
                        long j = (long)cy * w + cx;
                        if (known[j]) continue;
                        float z = v.Z[j];
                        if (float.IsInfinity(z) || z <= 0) z = 1e5f;
                        double weight = (double)z * z / step;
                        long p = j * 4;
                        sr += weight * src.P[p]; sg += weight * src.P[p + 1]; sb += weight * src.P[p + 2]; sw += weight;
                        break;
                    }
                }
                long op = i * 4;
                if (sw > 0)
                {
                    v.Colour.P[op] = (byte)Math.Clamp(sr / sw + 0.5, 0, 255);
                    v.Colour.P[op + 1] = (byte)Math.Clamp(sg / sw + 0.5, 0, 255);
                    v.Colour.P[op + 2] = (byte)Math.Clamp(sb / sw + 0.5, 0, 255);
                    v.Colour.P[op + 3] = 255;
                }
            }
        });
        // The eight rays leave a visible cross-hatch inside a large hole — each output pixel is an average of eight
        // point samples, and neighbouring pixels pick different ones. Three smoothing passes over the FILLED pixels
        // only (never over the real ones) remove the lattice without touching anything that was photographed.
        for (int pass = 0; pass < 3; pass++)
        {
            var snap = v.Colour.Clone();
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, holes.Count), range =>
            {
                for (int k = range.Item1; k < range.Item2; k++)
                {
                    long i = holes[k];
                    int x = (int)(i % w), y = (int)(i / w);
                    double s0 = 0, s1 = 0, s2 = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            long p2 = ((long)yy * w + xx) * 4;
                            s0 += snap.P[p2]; s1 += snap.P[p2 + 1]; s2 += snap.P[p2 + 2]; n++;
                        }
                    long p = i * 4;
                    v.Colour.P[p] = (byte)(s0 / n + 0.5); v.Colour.P[p + 1] = (byte)(s1 / n + 0.5); v.Colour.P[p + 2] = (byte)(s2 / n + 0.5);
                }
            });
        }
        foreach (var i in holes) v.Hole[i] = false;
        return holes.Count;
    }

    /// <summary>
    /// The low-frequency difference between what is already in the view and what the donor would put there, measured
    /// where the two overlap and carried into the hole.
    ///
    /// Without it a donor patch shows as a tonal seam. The module frames come off the per-module ISP and the export
    /// image off the display ISP, and the two disagree by more than a global mean-and-spread match can absorb — the
    /// disagreement is a curve, not an offset, so it varies across the picture. Estimating it on a 16-px grid and
    /// carrying only the smooth part keeps the donor's real detail while removing the step at the seam. This is
    /// seamless cloning with the gradient term dropped, which is enough here because the patches are small.
    /// </summary>
    static float[] LowFrequencyCorrection(View v, View donor, int w, int h)
    {
        const int Cell = 16, Passes = 5;
        const float Clamp = 24f;
        int cw = (w + Cell - 1) / Cell, ch = (h + Cell - 1) / Cell;
        var val = new float[(long)cw * ch * 3];
        var has = new bool[(long)cw * ch];
        // The per-cell statistic is a MEDIAN, not a mean, and it is clamped. The overlap between the view and the
        // donor also contains genuine *geometric* disagreement at every depth edge, and those differences are large;
        // a mean lets them set the correction and drags real colour into the patch. (Measured: the mean version cost
        // A4 2.6 dB where the median version gains.)
        Parallel.For(0, ch, cy =>
        {
            var buf = new List<float>[3] { new(), new(), new() };
            for (int cx = 0; cx < cw; cx++)
            {
                long c = (long)cy * cw + cx;
                for (int k = 0; k < 3; k++) buf[k].Clear();
                for (int y = cy * Cell; y < Math.Min(h, (cy + 1) * Cell); y++)
                    for (int x = cx * Cell; x < Math.Min(w, (cx + 1) * Cell); x++)
                    {
                        long i = (long)y * w + x;
                        if (v.Hole[i] || donor.Hole[i]) continue;
                        long p = i * 4;
                        for (int k = 0; k < 3; k++) buf[k].Add(v.Colour.P[p + k] - donor.Colour.P[p + k]);
                    }
                if (buf[0].Count < 24) continue;
                has[c] = true;
                for (int k = 0; k < 3; k++) { buf[k].Sort(); val[c * 3 + k] = Math.Clamp(buf[k][buf[k].Count / 2], -Clamp, Clamp); }
            }
        });
        // flood the estimate into the cells that had no overlap, then smooth
        for (int pass = 0; pass < Passes; pass++)
        {
            var nv = (float[])val.Clone(); var nh = (bool[])has.Clone();
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    long c = (long)y * cw + x;
                    double s0 = 0, s1 = 0, s2 = 0; int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= cw || yy >= ch) continue;
                            long j = (long)yy * cw + xx;
                            if (!has[j]) continue;
                            s0 += val[j * 3]; s1 += val[j * 3 + 1]; s2 += val[j * 3 + 2]; k++;
                        }
                    if (k == 0) continue;
                    nv[c * 3] = (float)(s0 / k); nv[c * 3 + 1] = (float)(s1 / k); nv[c * 3 + 2] = (float)(s2 / k); nh[c] = true;
                }
            val = nv; has = nh;
        }
        var o = new float[(long)w * h * 3];
        Parallel.For(0, h, y =>
        {
            double fy = (y + 0.5) / Cell - 0.5; int y0 = (int)Math.Floor(fy); double ty = fy - y0;
            int ya = Math.Clamp(y0, 0, ch - 1), yb = Math.Clamp(y0 + 1, 0, ch - 1);
            for (int x = 0; x < w; x++)
            {
                double fx = (x + 0.5) / Cell - 0.5; int x0 = (int)Math.Floor(fx); double tx = fx - x0;
                int xa = Math.Clamp(x0, 0, cw - 1), xb = Math.Clamp(x0 + 1, 0, cw - 1);
                long a = ((long)ya * cw + xa) * 3, b2 = ((long)ya * cw + xb) * 3, c2 = ((long)yb * cw + xa) * 3, d2 = ((long)yb * cw + xb) * 3;
                long p = ((long)y * w + x) * 3;
                for (int k = 0; k < 3; k++)
                    o[p + k] = (float)((val[a + k] * (1 - tx) + val[b2 + k] * tx) * (1 - ty) + (val[c2 + k] * (1 - tx) + val[d2 + k] * tx) * ty);
            }
        });
        return o;
    }
}
