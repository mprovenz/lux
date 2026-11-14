using Lux.App.Develop;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>
/// The fake capture's picture at any level: the thumbnail's scene (sky, sun, hills, ground) drawn at full size, plus detail that only the
/// finer levels resolve — fractal texture whose octaves grow with the level, trees, a house with window bars, a sign with small lettering
/// and a resolution star — so the pyramid's levels are visibly different. Adjustments tint it (exposure, contrast, white balance,
/// saturation …) through a colour filter, so slider moves show.
/// </summary>
internal static class FakeScene
{
    /// <summary>The scene's own units (the thumbnail's 400 × 300).</summary>
    public const int SceneWidth = 400, SceneHeight = 300;

    /// <summary>Paint a tile of the picture as shown into a new BGRA bitmap of <paramref name="w"/> × <paramref name="h"/>: the capture
    /// (<paramref name="captureW"/> × <paramref name="captureH"/>, held turned when <paramref name="orientation"/> is 90 / 270) mapped into the
    /// picture by <paramref name="captureToPicture"/> (turn, flip, crop), and the tile <paramref name="tilePicture"/> (picture pixels at full
    /// size) drawn at <paramref name="levelScale"/> picture pixels per tile pixel.</summary>
    public static SKBitmap PaintTile(int index, bool stacked, bool telephoto, int orientation, int captureW, int captureH, SKMatrix captureToPicture,
                                     float levelScale, SKRect tilePicture, int w, int h, SKColorFilter? filter, int octaves)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var layer = filter == null ? null : new SKPaint { ColorFilter = filter };
        if (layer != null) canvas.SaveLayer(layer);
        canvas.Scale(1 / levelScale);
        canvas.Translate(-tilePicture.Left, -tilePicture.Top);
        canvas.Concat(ref captureToPicture);
        // capture pixels → scene units (portrait captures: the upright scene stored sideways, as the camera stores them)
        bool portrait = orientation is 90 or 270;
        if (portrait)
        {
            canvas.Translate(captureW / 2f, captureH / 2f);
            canvas.RotateDegrees(-orientation);
            canvas.Scale(captureH / (float)SceneHeight, captureW / (float)SceneWidth);
            canvas.Translate(-SceneHeight / 2f, -SceneWidth / 2f);
            float horizon = FakeThumbnails.DrawScene(canvas, index, stacked, telephoto, SceneHeight, SceneWidth);
            DrawDetails(canvas, index, stacked, horizon, octaves, SceneHeight, SceneWidth);
        }
        else
        {
            canvas.Scale(captureW / (float)SceneWidth, captureH / (float)SceneHeight);
            float horizon = FakeThumbnails.DrawScene(canvas, index, stacked, telephoto, SceneWidth, SceneHeight);
            DrawDetails(canvas, index, stacked, horizon, octaves);
        }
        if (layer != null) canvas.Restore();
        canvas.Flush();
        return bmp;
    }

    /// <summary>Texture octaves a level resolves: the finest stays under about a quarter cycle per level pixel, so coarse levels are smooth
    /// and the fine ones show grain (a visible difference between the levels).</summary>
    public static int OctavesForLevel(int level) => Math.Max(1, 5 - level);

    /// <summary>Detail in scene units (400 × 300) below the horizon at <paramref name="horizon"/>.</summary>
    public static void DrawDetails(SKCanvas canvas, int index, bool stacked, float horizon, int octaves, int w = SceneWidth, int h = SceneHeight)
    {
        float wscale = w / 400f;
        var rnd = new Random(index * 131 + 7);
        // ground texture: fractal noise in scene units; more octaves = finer grain (only the fine levels draw them)
        using (var noise = SKShader.CreatePerlinNoiseFractalNoise(0.18f, 0.18f, octaves, index % 97))
        using (var tex = new SKPaint { Shader = noise, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(90) })
            canvas.DrawRect(0, horizon + 6, w, h - horizon - 6, tex);

        // trees along the ridge
        using var trunk = new SKPaint { Color = stacked ? new SKColor(0x14, 0x12, 0x10) : new SKColor(0x4a, 0x38, 0x28), IsAntialias = true };
        using var crown = new SKPaint { Color = stacked ? new SKColor(0x18, 0x22, 0x1c) : new SKColor(0x3c, 0x5a, 0x38), IsAntialias = true };
        using var crownLight = new SKPaint { Color = stacked ? new SKColor(0x22, 0x2c, 0x26) : new SKColor(0x5a, 0x7a, 0x48), IsAntialias = true };
        for (int i = 0; i < 26; i++)
        {
            float x = (float)rnd.NextDouble() * w, s = 4 + (float)rnd.NextDouble() * 9;
            float y = horizon + 4 + (float)rnd.NextDouble() * 22;
            canvas.DrawRect(x - s * 0.08f, y - s * 0.4f, s * 0.16f, s * 0.9f, trunk);
            canvas.DrawCircle(x, y - s * 0.8f, s * 0.55f, crown);
            canvas.DrawCircle(x - s * 0.15f, y - s * 0.95f, s * 0.25f, crownLight);
        }

        // a house with window bars (the bars are about 1 image pixel at full size)
        float hx = (40 + (float)rnd.NextDouble() * 180) * wscale, hy = horizon + 30, hw = 46, hh = 30;
        using (var wall = new SKPaint { Color = stacked ? new SKColor(0x3a, 0x34, 0x30) : new SKColor(0xd8, 0xcc, 0xb8), IsAntialias = true })
            canvas.DrawRect(hx, hy, hw, hh, wall);
        using (var roof = new SKPaint { Color = new SKColor(0x7a, 0x3a, 0x2e), IsAntialias = true })
        using (var path = new SKPath())
        {
            path.MoveTo(hx - 4, hy); path.LineTo(hx + hw / 2, hy - 16); path.LineTo(hx + hw + 4, hy); path.Close();
            canvas.DrawPath(path, roof);
        }
        using var glass = new SKPaint { Color = stacked ? new SKColor(0xf0, 0xc8, 0x70) : new SKColor(0x4a, 0x5a, 0x6a), IsAntialias = true };
        using var bar = new SKPaint { Color = new SKColor(0xf4, 0xf0, 0xe6), IsAntialias = true, StrokeWidth = 0.05f, Style = SKPaintStyle.Stroke };
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 4; c++)
            {
                float wx = hx + 4 + c * 10.5f, wy = hy + 4 + r * 13;
                canvas.DrawRect(wx, wy, 7, 8, glass);
                canvas.DrawLine(wx + 3.5f, wy, wx + 3.5f, wy + 8, bar);
                canvas.DrawLine(wx, wy + 4, wx + 7, wy + 4, bar);
            }

        // a sign whose lettering is legible only at full size
        float sx = hx + hw + 14, sy = hy + 8;
        using (var post = new SKPaint { Color = new SKColor(0x5a, 0x48, 0x38) })
            canvas.DrawRect(sx + 9, sy + 6, 1.2f, 14, post);
        using (var board = new SKPaint { Color = new SKColor(0xf2, 0xee, 0xe4), IsAntialias = true })
            canvas.DrawRect(sx, sy, 20, 7, board);
        using (var font = new SKFont(SKTypeface.Default, 1.5f))
        using (var ink = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22), IsAntialias = true })
        {
            canvas.DrawText($"L16_{493 + index:00000}", sx + 1.2f, sy + 2.6f, font, ink);
            canvas.DrawText("Lux · fake capture", sx + 1.2f, sy + 4.6f, font, ink);
            canvas.DrawText("all 16 modules", sx + 1.2f, sy + 6.4f, font, ink);
        }

        // a resolution star (64 wedges) on the ground
        float cx = (300 + (float)rnd.NextDouble() * 60) * wscale, cy = Math.Min(h - 22, horizon + 50), rad = 16;
        using (var white = new SKPaint { Color = new SKColor(0xee, 0xee, 0xee), IsAntialias = true })
            canvas.DrawCircle(cx, cy, rad, white);
        using (var black = new SKPaint { Color = new SKColor(0x1a, 0x1a, 0x1a), IsAntialias = true })
        using (var star = new SKPath())
        {
            for (int k = 0; k < 64; k += 2)
            {
                double a0 = k * Math.PI * 2 / 64, a1 = (k + 1) * Math.PI * 2 / 64;
                star.MoveTo(cx, cy);
                star.LineTo(cx + rad * (float)Math.Cos(a0), cy + rad * (float)Math.Sin(a0));
                star.LineTo(cx + rad * (float)Math.Cos(a1), cy + rad * (float)Math.Sin(a1));
                star.Close();
            }
            canvas.DrawPath(star, black);
        }
    }

    /// <summary>A colour filter approximating the adjustments relative to their defaults (exposure, contrast, highlights, shadows and clarity
    /// as a tone curve; temperature, tint, saturation and vibrance as a colour matrix). Null when nothing differs.</summary>
    public static SKColorFilter? Filter(IReadOnlyDictionary<string, double> values, IReadOnlyList<AdjustmentSpec> specs)
    {
        double V(string key) => values.TryGetValue(key, out var v) ? v : specs.First(s => s.Key == key).Default;
        double D(string key) => specs.First(s => s.Key == key).Default;
        double ev = V(AdjustmentKeys.Exposure), con = V(AdjustmentKeys.Contrast) / 100, hi = V(AdjustmentKeys.Highlights) / 100, sh = V(AdjustmentKeys.Shadows) / 100;
        double cla = V(AdjustmentKeys.Clarity) / 100, sat = V(AdjustmentKeys.Saturation) / 100, vib = V(AdjustmentKeys.Vibrance) / 100;
        double temp = V(AdjustmentKeys.Temperature), tint = V(AdjustmentKeys.Tint) - D(AdjustmentKeys.Tint);
        double warm = Math.Log2(temp / D(AdjustmentKeys.Temperature));
        bool tone = Math.Abs(ev) > 1e-6 || Math.Abs(con) > 1e-6 || Math.Abs(hi) > 1e-6 || Math.Abs(sh) > 1e-6 || Math.Abs(cla) > 1e-6;
        bool colour = Math.Abs(warm) > 1e-6 || Math.Abs(tint) > 1e-6 || Math.Abs(sat) > 1e-6 || Math.Abs(vib) > 1e-6;
        if (!tone && !colour) return null;

        var lut = new byte[256];
        double gain = Math.Pow(2, ev), k = 1 + con * 0.7 + cla * 0.25;
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0 * gain;
            x += sh * 0.35 * Math.Pow(Math.Max(0, 1 - x), 3);            // lift / deepen the shadows
            x -= hi * 0.35 * Math.Pow(Math.Clamp(x, 0, 1), 3);           // recover / push the highlights
            x = (x - 0.5) * k + 0.5;
            lut[i] = (byte)Math.Clamp(Math.Round(x * 255), 0, 255);
        }
        var curve = SKColorFilter.CreateTable(null, lut, lut, lut);
        if (!colour) return curve;
        float r = (float)(1 + 0.32 * warm), b = (float)(1 - 0.32 * warm), g = (float)(1 - tint / 150 * 0.22);
        float s = (float)Math.Max(0, 1 + sat + vib * 0.6);
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[] m =
        {
            r * (lr + (1 - lr) * s), r * (lg - lg * s), r * (lb - lb * s), 0, 0,
            g * (lr - lr * s), g * (lg + (1 - lg) * s), g * (lb - lb * s), 0, 0,
            b * (lr - lr * s), b * (lg - lg * s), b * (lb + (1 - lb) * s), 0, 0,
            0, 0, 0, 1, 0,
        };
        var matrix = SKColorFilter.CreateColorMatrix(m);
        return SKColorFilter.CreateCompose(matrix, curve);
    }
}
