using Ltpb;
using Lux.Engine.Imaging;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::A::RemoveVignettingGeneric&lt;vec4x32f,1&gt;` (dispatcher `18012feb0`, grid builder `FUN_18013cd40`, cell rects
/// `FUN_180134090`, tile lambda `18013d980`) = `LensShading:default` on the Color domain (`setLensShading` lambda_53
/// `1804184b0`, called with the payload float rect, the CapturedImage, `lens_shading.multiplier` (pipeline+0x1b30)
/// and the inverse flag (+0x1b34)). Gain grid: the module's `VignettingCharacterization` model for the capture's
/// mirror hall code (single model for fixed lenses; movable mirrors lerp the two neighbouring hall codes, SoT §4),
/// transformed to `g' = (g − 1)·m + 1` (or `((g − 1)·m + 1)/g` when inverse), then bilinearly interpolated over
/// the frame: node spacing `hspace = W_frame·sx/(cols−1)`, `vspace = H_frame·sy/(rows−1)` with `sx = W_tile/(x1−x0)`,
/// per pixel `fx = (x + (xoff − col·hspace))·(1/floor(hspace))`, gain = lerp(lerp(g00,g10,fy), lerp(g01,g11,fy), fx);
/// out.rgb = gain·in.rgb, alpha kept.
/// </summary>
public static class LensShadingKernel
{
    /// <summary>The (possibly hall-code-blended) grid for a module frame, before the multiplier transform.</summary>
    public static (int Cols, int Rows, float[] Data) ModelGrid(LightHeader h, CameraModule m)
    {
        VignettingCharacterization? vc = null;
        foreach (var cal in Calibration.ForModule(h, m.Id)) if (cal.Vignetting is not null && cal.Vignetting.Vignetting.Count > 0) vc = cal.Vignetting;
        if (vc is null) throw new InvalidOperationException("vignetting model not found!");
        var models = vc.Vignetting;
        if (models.Count == 1)
        {
            var v = models[0].Vignetting;
            return ((int)v.Width, (int)v.Height, v.Data.ToArray());
        }
        // movable mirror: neighbouring hall codes around the capture's mirror position (FUN_18013cd40 L26–300)
        int hall = m.MirrorPosition;   // CapturedImage+0x50 = CameraModule.mirror_position (NOT af_info.mirror_position: B1/B2/B3 of 00466 differ between the two — verified by the B stereo images 2026-08-27)
        var sorted = models.OrderBy(x => x.HallCode).ToList();
        var lo = sorted.LastOrDefault(x => (int)x.HallCode <= hall) ?? sorted[0];
        var hi = sorted.FirstOrDefault(x => (int)x.HallCode >= hall) ?? sorted[^1];
        var g0 = lo.Vignetting; var g1 = hi.Vignetting;
        if (lo == hi || g0.Data.Count == 0) return ((int)g0.Width, (int)g0.Height, g0.Data.ToArray());
        float t = ((float)hall - (float)(int)lo.HallCode) / (float)((int)hi.HallCode - (int)lo.HallCode), u = 1f - t;
        var d = new float[g0.Data.Count];
        for (int i = 0; i < d.Length; i++) d[i] = g0.Data[i] * u + g1.Data[i] * t;
        return ((int)g0.Width, (int)g0.Height, d);
    }

    /// <summary>`FUN_18013cd40` tail: `(g + (−1))·m + 1`, or `((g + (−1))·m + 1)/g` when inverse.</summary>
    public static float[] Transform(float[] grid, float m, bool inverse)
    {
        var o = new float[grid.Length];
        for (int i = 0; i < o.Length; i++) { float g = grid[i]; float v = (g + -1f) * m + 1f; o[i] = inverse ? v / g : v; }
        return o;
    }

    /// <summary>Apply to <paramref name="img"/> (frame coordinates <paramref name="rect"/> within a frame of
    /// <paramref name="frameW"/>×<paramref name="frameH"/>) in place. <paramref name="floatRect"/> is the payload's
    /// float rect (frame units) and <paramref name="tileW"/>/<paramref name="tileH"/> the payload int rect size.</summary>
    public static void Apply(Image<Vec4F> img, RectI rect, RectF floatRect, int tileW, int tileH, int frameW, int frameH, int cols, int rows, float[] grid)
    {
        float sx = (float)tileW / (floatRect.X1 - floatRect.X0), sy = (float)tileH / (floatRect.Y1 - floatRect.Y0);
        float xoff = floatRect.X0 * sx, yoff = floatRect.Y0 * sy;
        float hspace = ((float)frameW * sx) / (float)(cols - 1), vspace = ((float)frameH * sy) / (float)(rows - 1);
        float invH = 1f / MathF.Floor(hspace), invV = 1f / MathF.Floor(vspace);   // roundss imm 9 = floor
        int ox = (int)xoff, oy = (int)yoff;                                        // tile origin in frame pixels
        int col0 = (int)(xoff * (1f / hspace)), row0 = (int)(yoff * (1f / vspace));
        int col1 = (int)MathF.Ceiling(sx * floatRect.X1 * (1f / hspace)), row1 = (int)MathF.Ceiling(sy * floatRect.Y1 * (1f / vspace));
        for (int row = row0; row < row1; row++)
        {
            // cell rect (FUN_180134090): [(int)(row·vspace) − oy, (int)((row+1)·vspace) − oy) clamped to the tile
            int cy0 = Math.Max((int)((float)row * vspace) - oy, 0), cy1 = Math.Min((int)(vspace * (float)(row + 1)) - oy, tileH);
            for (int col = col0; col < col1; col++)
            {
                int cx0 = Math.Max((int)((float)col * hspace) - ox, 0), cx1 = Math.Min((int)(hspace * (float)(col + 1)) - ox, tileW);
                if (cy0 >= cy1 || cx0 >= cx1) continue;
                // Lumen indexes the grid without clamping (a last-column cell reads the next row's first node)
                float G(int i) => i >= 0 && i < grid.Length ? grid[i] : grid[^1];
                float g00 = G(row * cols + col), g01 = G(row * cols + col + 1), g10 = G((row + 1) * cols + col), g11 = G((row + 1) * cols + col + 1);
                for (int y = cy0; y < cy1; y++)
                {
                    float fy = ((float)y + (yoff - (float)row * vspace)) * invV;
                    float left = fy * (g10 - g00) + g00;
                    float right = fy * (g11 - g01) + g01;
                    float slope = (right - left) * invH;   // asm 18013daef–daf3: per-row (right − left)·invH
                    // asm 18013daf8–db63: the per-pixel gain is evaluated in double and rounded to float
                    double slopeD = slope, leftD = left, xdD = xoff - (float)col * hspace;
                    var span = img.Row(y + rect.Y0 - img.Rect.Y0);
                    for (int x = cx0; x < cx1; x++)
                    {
                        float gain = (float)(((double)x + xdD) * slopeD + leftD);
                        ref var p = ref span[x + rect.X0 - img.Rect.X0];
                        p.R *= gain; p.G *= gain; p.B *= gain;   // blendps …,8 keeps alpha
                    }
                }
            }
        }
    }
}

public static class LensShadingMono
{
    /// <summary>`RemoveVignettingGeneric&lt;float,1&gt;` (outer `180130760`, kernel `18013dc80`): the single-lane variant of
    /// <see cref="LensShadingKernel.Apply"/> for the mono SoftISP (spec `a4d803768fac564ad.md` §1.6) — same cell geometry, per-row float
    /// lerps, per-pixel gain in double rounded to float, `out = gain · in`. <paramref name="tileX"/>/<paramref name="tileY"/> = the tile
    /// origin in frame pixels (floatRect = the tile ROI, sx = sy = 1).</summary>
    public static void Apply(float[] img, int w, int h, int tileX, int tileY, int frameW, int frameH, int cols, int rows, float[] grid)
    {
        float sx = (float)w / ((float)(tileX + w) - (float)tileX), sy = (float)h / ((float)(tileY + h) - (float)tileY);
        float xoff = (float)tileX * sx, yoff = (float)tileY * sy;
        float hspace = ((float)frameW * sx) / (float)(cols - 1), vspace = ((float)frameH * sy) / (float)(rows - 1);
        float invH = 1f / MathF.Floor(hspace), invV = 1f / MathF.Floor(vspace);
        int ox = (int)xoff, oy = (int)yoff;
        int col0 = (int)(xoff * (1f / hspace)), row0 = (int)(yoff * (1f / vspace));
        int col1 = (int)MathF.Ceiling(sx * (float)(tileX + w) * (1f / hspace)), row1 = (int)MathF.Ceiling(sy * (float)(tileY + h) * (1f / vspace));
        for (int row = row0; row < row1; row++)
        {
            int cy0 = Math.Max((int)((float)row * vspace) - oy, 0), cy1 = Math.Min((int)(vspace * (float)(row + 1)) - oy, h);
            for (int col = col0; col < col1; col++)
            {
                int cx0 = Math.Max((int)((float)col * hspace) - ox, 0), cx1 = Math.Min((int)(hspace * (float)(col + 1)) - ox, w);
                if (cy0 >= cy1 || cx0 >= cx1) continue;
                float G(int i) => i >= 0 && i < grid.Length ? grid[i] : grid[^1];
                float g00 = G(row * cols + col), g01 = G(row * cols + col + 1), g10 = G((row + 1) * cols + col), g11 = G((row + 1) * cols + col + 1);
                float dyoff = yoff - (float)row * vspace, dxoff = xoff - (float)col * hspace;
                for (int y = cy0; y < cy1; y++)
                {
                    float fy = ((float)y + dyoff) * invV;
                    float left = fy * (g10 - g00) + g00, right = fy * (g11 - g01) + g01;
                    float slope = (right - left) * invH;
                    double slopeD = slope, leftD = left, xdD = dxoff;
                    for (int x = cx0; x < cx1; x++)
                    {
                        float gain = (float)(((double)x + xdD) * slopeD + leftD);
                        img[y * w + x] = gain * img[y * w + x];
                    }
                }
            }
        }
    }
}

public sealed class LensShadingStage : IStage
{
    private readonly bool _inverse;
    public LensShadingStage(bool inverse) { _inverse = inverse; }
    public StageName Stage => StageName.LensShading;
    public string TypeString => _inverse ? "inverse" : "default";
    public StageMeta Meta => new(1, 1, 1f);
    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("LensShading needs the RGB working image");
        var t = p.Context.Tuning;
        float m = (float)t.Num("lens_shading.multiplier");
        var (cols, rows, grid) = LensShadingKernel.ModelGrid(p.Context.Header, p.Context.Module);
        var g = LensShadingKernel.Transform(grid, m, _inverse);
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        LensShadingKernel.Apply(img, abs, p.FloatRect, p.IntRect.Width, p.IntRect.Height, p.Context.FrameWidth, p.Context.FrameHeight, cols, rows, g);
    }
}
