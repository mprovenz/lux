using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>Paints a synthetic "photo" (sky, hills, ground, a sun) per capture and encodes it as JPEG, the contract's thumbnail format.</summary>
internal static class FakeThumbnails
{
    public const int Width = 400, Height = 300;

    /// <summary>A capture's thumbnail as the library delivers it: for a portrait capture (<paramref name="orientation"/> 90 / 270) the scene is
    /// stored sideways, and the app turns it upright.</summary>
    public static byte[] MakeJpeg(int index, bool stacked, bool telephoto, int orientation = 0, SKColorFilter? filter = null)
    {
        if (orientation is 90 or 270)
        {
            using var upright = Paint(index, stacked, telephoto, Height, Width, filter);
            using var sideways = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var cv = sideways.Canvas;
            cv.Translate(Width / 2f, Height / 2f);
            cv.RotateDegrees(-orientation);
            cv.DrawImage(upright, -Height / 2f, -Width / 2f);
            using var img = sideways.Snapshot();
            using var d = img.Encode(SKEncodedImageFormat.Jpeg, 82);
            return d.ToArray();
        }
        using var image = Paint(index, stacked, telephoto, Width, Height, filter);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        return data.ToArray();
    }

    static SKImage Paint(int index, bool stacked, bool telephoto, int Width, int Height, SKColorFilter? filter)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using var layer = filter == null ? null : new SKPaint { ColorFilter = filter };
        if (layer != null) canvas.SaveLayer(layer);
        float horizon = DrawScene(canvas, index, stacked, telephoto, Width, Height);
        // the viewer's detail (trees, a house, a sign, a resolution star) at thumbnail size, so the stand-in matches the tiles
        canvas.Save();
        canvas.Scale(Width / (float)FakeScene.SceneWidth, Height / (float)FakeScene.SceneHeight);
        FakeScene.DrawDetails(canvas, index, stacked, horizon * FakeScene.SceneHeight / Height, octaves: 1);
        canvas.Restore();
        if (layer != null) canvas.Restore();
        return surface.Snapshot();
    }

    /// <summary>The macro scene (sky, sun or moon, hills, ground) in a <paramref name="Width"/> × <paramref name="Height"/> canvas; returns the
    /// horizon's y. Shared with the viewer's fake tiles (drawn there at full size).</summary>
    internal static float DrawScene(SKCanvas canvas, int index, bool stacked, bool telephoto, int Width, int Height)
    {
        var rnd = new Random(index * 7717 + 3);
        // Palette: one of seven moods; night (stacked) is dark blue.
        (SKColor top, SKColor horizon, SKColor ground, SKColor hill)[] moods =
        {
            (new SKColor(0x5d, 0x7f, 0xa3), new SKColor(0xc9, 0xd6, 0xde), new SKColor(0x6b, 0x7a, 0x52), new SKColor(0x55, 0x66, 0x5e)),
            (new SKColor(0x8a, 0x6e, 0x5a), new SKColor(0xe8, 0xb8, 0x82), new SKColor(0x5e, 0x4a, 0x3a), new SKColor(0x7a, 0x5e, 0x4a)),
            (new SKColor(0x6a, 0x8e, 0x8a), new SKColor(0xd8, 0xe4, 0xd8), new SKColor(0x4e, 0x6e, 0x4a), new SKColor(0x5a, 0x7a, 0x62)),
            (new SKColor(0x72, 0x66, 0x8e), new SKColor(0xe0, 0xc4, 0xd2), new SKColor(0x4a, 0x44, 0x5a), new SKColor(0x66, 0x5a, 0x76)),
            (new SKColor(0x4f, 0x6d, 0x9a), new SKColor(0xb4, 0xc8, 0xe2), new SKColor(0x7e, 0x86, 0x8e), new SKColor(0x6a, 0x74, 0x82)),
            (new SKColor(0x9a, 0x82, 0x60), new SKColor(0xf0, 0xd6, 0xa8), new SKColor(0x8a, 0x70, 0x4a), new SKColor(0xa0, 0x84, 0x5c)),
            (new SKColor(0x5a, 0x6e, 0x78), new SKColor(0xa8, 0xb8, 0xbe), new SKColor(0x3e, 0x52, 0x4a), new SKColor(0x52, 0x66, 0x5e)),
        };
        var m = moods[index % moods.Length];
        if (stacked) m = (new SKColor(0x10, 0x16, 0x2c), new SKColor(0x3a, 0x40, 0x66), new SKColor(0x1a, 0x1c, 0x24), new SKColor(0x22, 0x26, 0x36));
        float horizonY = Height * (0.45f + (float)rnd.NextDouble() * 0.2f);
        using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, horizonY), new[] { m.top, m.horizon }, SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, Width, horizonY + 1, sky);
        // Sun or moon
        using (var sun = new SKPaint { Color = stacked ? new SKColor(0xe8, 0xe8, 0xf0) : new SKColor(0xff, 0xf2, 0xd0, 0xd0), IsAntialias = true })
            canvas.DrawCircle(Width * (0.15f + (float)rnd.NextDouble() * 0.7f), horizonY * (0.25f + (float)rnd.NextDouble() * 0.4f), telephoto ? 34 : 14, sun);
        // Hills: two layers of soft ridges
        for (int layer = 0; layer < 2; layer++)
        {
            using var path = new SKPath();
            float baseY = horizonY + layer * 18;
            path.MoveTo(0, Height);
            path.LineTo(0, baseY);
            int n = 6;
            for (int k = 1; k <= n; k++)
            {
                float x = Width * k / (float)n;
                float y = baseY - (float)rnd.NextDouble() * (telephoto ? 70 : 40) * (layer == 0 ? 1 : 0.6f);
                path.QuadTo(x - Width / (2f * n), y - 20, x, y);
            }
            path.LineTo(Width, Height);
            path.Close();
            var c = layer == 0 ? m.hill : m.ground;
            using var p = new SKPaint { Color = c, IsAntialias = true };
            canvas.DrawPath(path, p);
        }
        // Foreground
        using (var fg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, horizonY + 30), new SKPoint(0, Height), new[] { m.ground, Darken(m.ground, 0.55f) }, SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, horizonY + 30, Width, Height, fg);
        return horizonY;
    }

    static SKColor Darken(SKColor c, float f) => new((byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f));
}
