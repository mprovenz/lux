namespace Lux.Engine.Pipeline.Parallax;

/// <summary>The result of one synthesised view: colour, the destination depth (the z-buffer that produced it) and the
/// hole mask. Holes are kept rather than filled in the warper so a caller can decide where the fill comes from — the
/// other real modules first, inpainting only as a fallback.</summary>
public sealed class View
{
    public required Rgba Colour;
    public required float[] Z;        // per destination pixel, +inf where nothing landed
    public required bool[] Hole;      // true where no source sample landed at all
    public double Tx, Ty;             // the virtual camera translation, mm, that produced it (0 for non-translation warps)
    public int HoleCount => Hole.Count(h => h);
    public double HolePercent(int w, int h) => 100.0 * HoleCount / ((long)w * h);
}

/// <summary>
/// Depth-image-based rendering: the whole of the parallax formats' geometry lives here.
///
/// A virtual camera translated by (tx,ty) millimetres from the reference, with no rotation, moves the image of a point
/// at depth Z by <c>dx = −f·tx/Z</c>, <c>dy = −f·ty/Z</c> — the ordinary parallax relation, which the L16's own
/// numbers already satisfy. With a convergence plane Z₀ the shift becomes <c>−f·t·(1/Z − 1/Z₀)</c>, so everything at
/// Z₀ stays exactly where it is and the scene swings around it. That is the analytic replacement for the wigglegram's
/// phase-correlation pivot: exact, and chosen rather than discovered.
///
/// The warp is a **forward splat with a z-buffer** (nearer wins). Forward, not backward, because the destination depth
/// is not known in advance; splat with a per-pixel footprint, not a point, because a surface that stretches under the
/// warp otherwise breaks into one-pixel cracks. The footprint is the interval between the midpoints to the horizontal
/// and vertical neighbours' destinations — but only across neighbours at a similar depth, so the fill never bridges an
/// occlusion boundary. What is left uncovered is a genuine disocclusion and is reported in <see cref="View.Hole"/>.
///
/// Every parallax effect is one <see cref="Map"/> away from every other: the parallax translation, the dolly zoom's
/// radial scaling and the multi-view donor warps are all the same splatter with a different displacement rule.
/// </summary>
public static class Dibr
{
    /// <summary>Displacement of one source pixel: (x, y, Z in mm) → the destination position, in destination pixels.</summary>
    public delegate (float X, float Y) Map(int x, int y, float z);

    /// <summary>Depths within this fraction of each other count as the same surface: for the z-buffer test and for
    /// deciding whether a footprint may stretch to a neighbour.</summary>
    public const float DepthTol = 0.03f;

    /// <summary>Largest footprint half-extent, in destination pixels. A surface stretching more than this is nearly
    /// edge-on to the new camera and smearing it is worse than leaving the hole.</summary>
    public const float MaxExtent = 12f;

    /// <summary>Translate the camera by (tx,ty) mm with a convergence plane at <paramref name="convergeZmm"/>
    /// (0 or infinite = none), plus an optional constant offset — which the multi-view donors need, because a donor's
    /// pixels already carry that module's own parallax and only the *difference* has to be applied.</summary>
    public static View Synthesise(Rgba colour, Plane depth, double focalPx, double tx, double ty,
                                  double convergeZmm, double offX = 0, double offY = 0)
    {
        double invZ0 = convergeZmm > 0 && !double.IsInfinity(convergeZmm) ? 1.0 / convergeZmm : 0.0;
        double kx = -focalPx * tx, ky = -focalPx * ty;
        double cx = offX + focalPx * tx * invZ0, cy = offY + focalPx * ty * invZ0;
        var v = Warp(colour, depth, (x, y, z) => ((float)(x + kx / z + cx), (float)(y + ky / z + cy)));
        v.Tx = tx; v.Ty = ty;
        return v;
    }

    public static View Synthesise(ParallaxSource src, double tx, double ty, double convergeZmm)
        => Synthesise(src.Colour, src.Depth, src.Geometry.FocalPx, tx, ty, convergeZmm);

    /// <summary>
    /// Dolly zoom: the camera moves <paramref name="dzMm"/> millimetres along its own axis while the focal length is
    /// scaled to hold a subject at <paramref name="subjectZmm"/> the same size. A point at Z projects
    /// <c>Z/(Z−dz)</c> larger after the move, and the compensating zoom is <c>(Z_s−dz)/Z_s</c>, so the net is a
    /// per-pixel radial magnification about the principal point that is exactly 1 at the subject and falls away from
    /// it in both directions. Moving in and zooming out expands the background; the reverse compresses it.
    /// </summary>
    public static View DollyZoom(Rgba colour, Plane depth, double dzMm, double subjectZmm)
    {
        double cx = colour.W * 0.5, cy = colour.H * 0.5;
        double zoom = (subjectZmm - dzMm) / subjectZmm;
        return Warp(colour, depth, (x, y, z) =>
        {
            double zz = z - dzMm;
            if (zz < 1) zz = 1;                       // a point behind the new camera has no image; clamp rather than fold
            double m = (z / zz) * zoom;
            return ((float)(cx + (x - cx) * m), (float)(cy + (y - cy) * m));
        });
    }

    /// <summary>The splatter itself.</summary>
    public static View Warp(Rgba colour, Plane depth, Map map)
    {
        int W = colour.W, H = colour.H;
        var dx = new float[(long)W * H];
        var dy = new float[(long)W * H];
        var valid = new bool[(long)W * H];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                long i = (long)y * W + x;
                float z = depth.V[i];
                if (!(z > 0f) || float.IsNaN(z)) { valid[i] = false; dx[i] = x; dy[i] = y; continue; }
                var (px, py) = map(x, y, z);
                dx[i] = px; dy[i] = py; valid[i] = true;
            }
        });

        var zbuf = new float[(long)W * H];
        var acc = new float[(long)W * H * 3];
        var wsum = new float[(long)W * H];
        Array.Fill(zbuf, float.PositiveInfinity);

        // Bound the vertical reach so destination bands can be filled without locking: the largest |dy| shift any
        // pixel takes, plus the footprint.
        float maxDy = 0f;
        for (int y = 0; y < H; y++)
        {
            long r = (long)y * W;
            for (int x = 0; x < W; x++) { float d = Math.Abs(dy[r + x] - y); if (d > maxDy) maxDy = d; }
        }
        int reach = (int)Math.Ceiling(Math.Min(maxDy, H) + MaxExtent + 2);

        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, H / 32));
        int bandH = (H + bands - 1) / bands;
        Parallel.For(0, bands, b =>
        {
            int by0 = b * bandH, by1 = Math.Min(H, by0 + bandH);
            if (by0 >= by1) return;
            int sy0 = Math.Max(0, by0 - reach), sy1 = Math.Min(H, by1 + reach);
            for (int y = sy0; y < sy1; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    long i = (long)y * W + x;
                    if (!valid[i]) continue;
                    float z = depth.V[i];
                    float px = dx[i], py = dy[i];
                    float ex = Extent(dx, depth.V, valid, i, 1, x > 0, x < W - 1, z);
                    float ey = Extent(dy, depth.V, valid, i, W, y > 0, y < H - 1, z);

                    int ix0 = (int)Math.Ceiling(px - ex - 0.5f), ix1 = (int)Math.Floor(px + ex + 0.5f);
                    int iy0 = (int)Math.Ceiling(py - ey - 0.5f), iy1 = (int)Math.Floor(py + ey + 0.5f);
                    if (ix1 < 0 || ix0 >= W) continue;
                    if (ix0 < 0) ix0 = 0; if (ix1 >= W) ix1 = W - 1;
                    if (iy0 < by0) iy0 = by0; if (iy1 >= by1) iy1 = by1 - 1;
                    if (iy1 < iy0) continue;

                    long sp = i * 4;
                    float r = colour.P[sp], g = colour.P[sp + 1], bl = colour.P[sp + 2];
                    if (colour.P[sp + 3] == 0) continue;      // a source pixel outside the donor's own coverage
                    for (int dyi = iy0; dyi <= iy1; dyi++)
                    {
                        long ro = (long)dyi * W;
                        for (int dxi = ix0; dxi <= ix1; dxi++)
                        {
                            long j = ro + dxi;
                            float zb = zbuf[j];
                            float tol = zb * DepthTol;
                            if (z < zb - tol)
                            {   // a nearer surface: this destination pixel belongs to it, discard what was there
                                zbuf[j] = z; wsum[j] = 1f;
                                acc[j * 3] = r; acc[j * 3 + 1] = g; acc[j * 3 + 2] = bl;
                            }
                            else if (z <= zb + tol)
                            {
                                zbuf[j] = Math.Min(zb, z);
                                wsum[j] += 1f;
                                acc[j * 3] += r; acc[j * 3 + 1] += g; acc[j * 3 + 2] += bl;
                            }
                        }
                    }
                }
            }
        });

        var outImg = new Rgba(W, H);
        var hole = new bool[(long)W * H];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                long j = (long)y * W + x, p = j * 4;
                float w = wsum[j];
                if (w <= 0f) { hole[j] = true; outImg.P[p + 3] = 0; continue; }
                float inv = 1f / w;
                outImg.P[p] = (byte)Math.Clamp(acc[j * 3] * inv + 0.5f, 0, 255);
                outImg.P[p + 1] = (byte)Math.Clamp(acc[j * 3 + 1] * inv + 0.5f, 0, 255);
                outImg.P[p + 2] = (byte)Math.Clamp(acc[j * 3 + 2] * inv + 0.5f, 0, 255);
                outImg.P[p + 3] = 255;
            }
        });
        return new View { Colour = outImg, Z = zbuf, Hole = hole };
    }

    /// <summary>Half-extent of a source pixel's destination footprint along one axis: half the distance to whichever
    /// same-surface neighbour is farther away in the destination, floored at 0.5 (a pixel always covers itself).</summary>
    static float Extent(float[] d, float[] z, bool[] valid, long i, int step, bool hasPrev, bool hasNext, float zc)
    {
        float e = 0.5f;
        float tol = zc * DepthTol * 4f;    // looser than the z-buffer's: this only decides "same surface"
        if (hasPrev && valid[i - step] && Math.Abs(z[i - step] - zc) <= tol) e = Math.Max(e, 0.5f * Math.Abs(d[i] - d[i - step]));
        if (hasNext && valid[i + step] && Math.Abs(z[i + step] - zc) <= tol) e = Math.Max(e, 0.5f * Math.Abs(d[i + step] - d[i]));
        return Math.Min(e, MaxExtent);
    }

    /// <summary>Read a depth at an image point, median-filtered over a small window so a single bad depth pixel does
    /// not set the convergence plane for a whole animation. Coordinates are the working image's own pixels.</summary>
    public static double DepthAt(Plane d, int x, int y, int radius = 7)
    {
        var v = new List<float>();
        for (int yy = Math.Max(0, y - radius); yy <= Math.Min(d.H - 1, y + radius); yy++)
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(d.W - 1, x + radius); xx++)
            { float z = d[xx, yy]; if (z > 0f && !float.IsNaN(z)) v.Add(z); }
        if (v.Count == 0) return 0;
        v.Sort();
        return v[v.Count / 2];
    }
}
