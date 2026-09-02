namespace Lux.Engine.Pipeline.Parallax;

/// <summary>
/// Depth effects that do not move the camera.
///
/// <b>Synthetic depth of field.</b> The blur diameter of a point at depth Z, for a lens of aperture diameter A focused
/// at Z_f, is <c>c = A·f·|1/Z − 1/Z_f|</c> with f in pixels — the ordinary thin-lens circle of confusion, and the
/// reason the control here is an aperture in millimetres rather than a blur radius: it makes the effect a statement
/// about a lens the L16 does not have. f/1.4 on the A group's 28 mm-equivalent is a 20 mm aperture, which is the
/// default.
///
/// The implementation is a **layered composite**, not a per-pixel variable blur. A per-pixel blur radius pulls sharp
/// foreground pixels into the background and leaves a dark halo around every silhouette, because it gathers rather
/// than scatters. Splitting the scene into depth layers, blurring each with its own radius *including its alpha*, and
/// compositing far to near gets the two things a lens does right: a blurred foreground spreads over what is behind it,
/// and a sharp foreground does not smear into a blurred background. Layer membership is triangular over 1/Z, so a
/// surface spanning two layers crossfades between them instead of tearing at the boundary.
///
/// The blur itself is a true **disc**, computed with a per-row prefix sum (each output row sums horizontal spans of
/// half-width √(r²−dy²)). A separable Gaussian would be several times faster and would look like a blur filter; a disc
/// looks like an aperture.
/// </summary>
public static class Effects
{
    /// <summary>Circle-of-confusion diameter in pixels for a point at <paramref name="z"/> mm.</summary>
    public static double Coc(double z, double focusZ, double apertureMm, double focalPx)
        => z <= 0 ? 0 : apertureMm * focalPx * Math.Abs(1.0 / z - 1.0 / focusZ);

    /// <param name="apertureMm">aperture DIAMETER in mm (f/1.4 on the 28 mm-equivalent A group is 20 mm)</param>
    /// <param name="maxRadius">clamp, in px; beyond this the cost grows without the picture changing much</param>
    public static Rgba DepthOfField(Rgba colour, Plane depth, double focalPx, double focusZmm, double apertureMm,
                                    int layers = 8, double maxRadius = 64)
    {
        int W = colour.W, H = colour.H;
        // Layer boundaries are uniform in 1/Z (disparity), which is where the eye's and the lens's sensitivity both
        // live: splitting uniformly in Z would give every layer to the background.
        float zmin = float.MaxValue, zmax = 0f;
        for (long i = 0; i < depth.V.LongLength; i++)
        { float z = depth.V[i]; if (z > 0 && !float.IsNaN(z)) { if (z < zmin) zmin = z; if (z > zmax) zmax = z; } }
        if (zmax <= 0) return colour.Clone();
        double dmin = 1.0 / Math.Max(zmax, 1), dmax = 1.0 / Math.Max(zmin, 1);
        if (dmax - dmin < 1e-9) return colour.Clone();

        var acc = new float[(long)W * H * 4];     // premultiplied RGB + alpha, composited far to near
        var layerZ = new double[layers];
        for (int l = 0; l < layers; l++) layerZ[l] = 1.0 / (dmin + (dmax - dmin) * (l + 0.5) / layers);

        var rgb = new float[(long)W * H * 4];
        for (int l = 0; l < layers; l++)          // l = 0 is the farthest layer
        {
            double r = Math.Min(maxRadius, 0.5 * Coc(layerZ[l], focusZmm, apertureMm, focalPx));
            Array.Clear(rgb);
            long covered = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    long i = (long)y * W + x;
                    float z = depth.V[i];
                    double d = z > 0 && !float.IsNaN(z) ? 1.0 / z : dmin;
                    double u = (d - dmin) / (dmax - dmin) * layers - 0.5;
                    double wgt = Math.Max(0, 1.0 - Math.Abs(u - l));   // triangular membership, so layers crossfade
                    if (wgt <= 0) continue;
                    long p = i * 4;
                    rgb[p] = (float)(colour.P[p] * wgt); rgb[p + 1] = (float)(colour.P[p + 1] * wgt);
                    rgb[p + 2] = (float)(colour.P[p + 2] * wgt); rgb[p + 3] = (float)wgt;
                    covered++;
                }
            if (covered == 0) continue;
            var blurred = r >= 0.5 ? Disc(rgb, W, H, (int)Math.Round(r)) : rgb;
            // over-composite: the nearer layer covers what is behind it in proportion to its own alpha
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    long p = ((long)y * W + x) * 4;
                    float a = Math.Clamp(blurred[p + 3], 0f, 1f);
                    for (int c = 0; c < 3; c++) acc[p + c] = blurred[p + c] + acc[p + c] * (1 - a);
                    acc[p + 3] = a + acc[p + 3] * (1 - a);
                }
            });
        }
        var o = new Rgba(W, H);
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                long p = ((long)y * W + x) * 4;
                float a = acc[p + 3];
                float k = a > 1e-3f ? 1f / a : 0f;
                for (int c = 0; c < 3; c++) o.P[p + c] = (byte)Math.Clamp(acc[p + c] * k + 0.5f, 0, 255);
                o.P[p + 3] = 255;
            }
        });
        return o;
    }

    /// <summary>Uniform disc blur of a premultiplied RGBA float image, via a per-row prefix sum: each output pixel is
    /// the sum of horizontal spans of half-width √(r²−dy²), which is a circle rather than a square or a Gaussian.</summary>
    public static float[] Disc(float[] src, int w, int h, int r)
    {
        if (r < 1) return src;
        var pre = new double[(long)h * (w + 1) * 4];
        Parallel.For(0, h, y =>
        {
            long ro = (long)y * (w + 1) * 4;
            for (int c = 0; c < 4; c++) pre[ro + c] = 0;
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++) pre[ro + (long)(x + 1) * 4 + c] = pre[ro + (long)x * 4 + c] + src[((long)y * w + x) * 4 + c];
        });
        var spans = new int[2 * r + 1];
        double area = 0;
        for (int dy = -r; dy <= r; dy++) { spans[dy + r] = (int)Math.Floor(Math.Sqrt(Math.Max(0, (double)r * r - (double)dy * dy))); area += 2 * spans[dy + r] + 1; }
        var o = new float[src.Length];
        float inv = (float)(1.0 / area);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    int hx = spans[dy + r];
                    int x0 = Math.Max(0, x - hx), x1 = Math.Min(w - 1, x + hx);
                    long ro = (long)yy * (w + 1) * 4;
                    s0 += pre[ro + (long)(x1 + 1) * 4] - pre[ro + (long)x0 * 4];
                    s1 += pre[ro + (long)(x1 + 1) * 4 + 1] - pre[ro + (long)x0 * 4 + 1];
                    s2 += pre[ro + (long)(x1 + 1) * 4 + 2] - pre[ro + (long)x0 * 4 + 2];
                    s3 += pre[ro + (long)(x1 + 1) * 4 + 3] - pre[ro + (long)x0 * 4 + 3];
                }
                long p = ((long)y * w + x) * 4;
                o[p] = (float)s0 * inv; o[p + 1] = (float)s1 * inv; o[p + 2] = (float)s2 * inv; o[p + 3] = (float)s3 * inv;
            }
        });
        return o;
    }
}

/// <summary>
/// Stereo pair presentation.
///
/// The widest physical colour baseline on the camera is A4→A5 at 71.49 mm, so a human interocular distance (63 mm by
/// convention) is *inside* the range the modules themselves cover — the pair can be synthesised without extrapolating
/// past anything the rig has seen, which is unusual for depth-based stereo.
///
/// The anaglyph matrices are Dubois's least-squares red/cyan projection rather than a naive channel swap. A naive
/// anaglyph leaves each eye a strong ghost of the other's image, because the red filter passes some of the cyan
/// channel and vice versa; Dubois solves for the pair of 3×3 matrices that minimise the perceived error through
/// measured filter spectra. It is applied in **linear light**, since it is a physical mixing model and the export
/// image is sRGB-encoded.
/// </summary>
public static class StereoView
{
    static readonly double[] DuboisL =
    {
         0.4155,  0.4710,  0.1670,
        -0.0458, -0.0484, -0.0257,
        -0.0545, -0.0614,  0.0128,
    };
    static readonly double[] DuboisR =
    {
        -0.0109, -0.0364, -0.0060,
         0.3756,  0.7333,  0.0111,
        -0.0651, -0.1287,  1.2971,
    };

    static double ToLinear(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    static byte ToSrgb(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(s * 255.0 + 0.5, 0, 255);
    }

    public static Rgba Anaglyph(Rgba left, Rgba right, string kind = "dubois")
    {
        int W = left.W, H = left.H;
        var o = new Rgba(W, H);
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                long p = ((long)y * W + x) * 4;
                if (kind == "grey")
                {
                    double gl = 0.299 * left.P[p] + 0.587 * left.P[p + 1] + 0.114 * left.P[p + 2];
                    double gr = 0.299 * right.P[p] + 0.587 * right.P[p + 1] + 0.114 * right.P[p + 2];
                    o.P[p] = (byte)Math.Clamp(gl + 0.5, 0, 255); o.P[p + 1] = (byte)Math.Clamp(gr + 0.5, 0, 255); o.P[p + 2] = (byte)Math.Clamp(gr + 0.5, 0, 255);
                }
                else if (kind == "colour" || kind == "color")
                { o.P[p] = left.P[p]; o.P[p + 1] = right.P[p + 1]; o.P[p + 2] = right.P[p + 2]; }
                else
                {
                    double lr = ToLinear(left.P[p]), lg = ToLinear(left.P[p + 1]), lb = ToLinear(left.P[p + 2]);
                    double rr = ToLinear(right.P[p]), rg = ToLinear(right.P[p + 1]), rb = ToLinear(right.P[p + 2]);
                    for (int c = 0; c < 3; c++)
                    {
                        double v = DuboisL[c * 3] * lr + DuboisL[c * 3 + 1] * lg + DuboisL[c * 3 + 2] * lb
                                 + DuboisR[c * 3] * rr + DuboisR[c * 3 + 1] * rg + DuboisR[c * 3 + 2] * rb;
                        o.P[p + c] = ToSrgb(v);
                    }
                }
                o.P[p + 3] = 255;
            }
        });
        return o;
    }

    /// <summary>Side by side. <paramref name="cross"/> puts the right eye's view on the left, for free-viewing by
    /// crossing the eyes rather than by diverging them.</summary>
    public static Rgba SideBySide(Rgba left, Rgba right, bool cross)
    {
        var a = cross ? right : left; var b = cross ? left : right;
        var o = new Rgba(a.W * 2, a.H);
        for (int y = 0; y < a.H; y++)
        {
            Buffer.BlockCopy(a.P, y * a.W * 4, o.P, y * o.W * 4, a.W * 4);
            Buffer.BlockCopy(b.P, y * b.W * 4, o.P, y * o.W * 4 + a.W * 4, b.W * 4);
        }
        return o;
    }
}
