namespace Lux.Engine.Imaging;

/// <summary>
/// Gradient-inverse edge-directed demosaic — the algorithm family of Light's DemosaickLightV2 (§3):
/// interpolate green with inverse-gradient direction weights, then R/B via colour-difference (channel−G).
/// Input is a normalized Bayer plane with the red pixel at (rx, ry); output is interleaved RGB (H·W·3).
/// </summary>
public static class Demosaic
{
    private const float Eps = 0.02f;

    public static float[] GradientInverse(float[] bayer, int w, int h, int rx, int ry)
    {
        var green = new float[(long)h * w];

        static int Clamp(int v, int hi) => v < 0 ? 0 : (v >= hi ? hi - 1 : v);
        float At(int y, int x) => bayer[(long)Clamp(y, h) * w + Clamp(x, w)];

        // Pass 1 — green everywhere (measured at green sites; inverse-gradient interp at R/B sites)
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int px = (x - rx) & 1, py = (y - ry) & 1;
                bool isGreen = !((px == 0 && py == 0) || (px == 1 && py == 1));
                float c = bayer[(long)y * w + x];
                if (isGreen) { green[(long)y * w + x] = c; continue; }
                float gN = At(y - 1, x), gS = At(y + 1, x), gE = At(y, x - 1), gW = At(y, x + 1);
                float cN = At(y - 2, x), cS = At(y + 2, x), cE = At(y, x - 2), cW = At(y, x + 2);
                float wN = 1f / (MathF.Abs(cN - c) + Eps), wS = 1f / (MathF.Abs(cS - c) + Eps);
                float wE = 1f / (MathF.Abs(cE - c) + Eps), wW = 1f / (MathF.Abs(cW - c) + Eps);
                green[(long)y * w + x] = (wN * gN + wS * gS + wE * gE + wW * gW) / (wN + wS + wE + wW);
            }

        // Pass 2 — R and B via colour-difference interpolation with a 3×3 [1 2 1;2 4 2;1 2 1] kernel
        var rgb = new float[(long)h * w * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int px = (x - rx) & 1, py = (y - ry) & 1;
                bool isR = px == 0 && py == 0, isB = px == 1 && py == 1;
                long p = (long)y * w + x;
                float g = green[p];
                float rval = isR ? bayer[p] : g + InterpDiff(bayer, green, w, h, x, y, rx, ry, red: true);
                float bval = isB ? bayer[p] : g + InterpDiff(bayer, green, w, h, x, y, rx, ry, red: false);
                long o = p * 3;
                rgb[o] = MathF.Max(rval, 0f);
                rgb[o + 1] = MathF.Max(g, 0f);
                rgb[o + 2] = MathF.Max(bval, 0f);
            }
        return rgb;
    }

    // weighted mean of (channel − green) over the 3×3 neighbourhood where the target channel is present
    private static readonly int[] Kdy = { -1, -1, -1, 0, 0, 0, 1, 1, 1 };
    private static readonly int[] Kdx = { -1, 0, 1, -1, 0, 1, -1, 0, 1 };
    private static readonly float[] Kw = { 1, 2, 1, 2, 4, 2, 1, 2, 1 };

    private static float InterpDiff(float[] bayer, float[] green, int w, int h, int x, int y, int rx, int ry, bool red)
    {
        float num = 0f, den = 0f;
        for (int k = 0; k < 9; k++)
        {
            int yy = y + Kdy[k], xx = x + Kdx[k];
            if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
            int px = (xx - rx) & 1, py = (yy - ry) & 1;
            bool present = red ? (px == 0 && py == 0) : (px == 1 && py == 1);
            if (!present) continue;
            long o = (long)yy * w + xx;
            num += Kw[k] * (bayer[o] - green[o]);
            den += Kw[k];
        }
        return den > 0f ? num / den : 0f;
    }
}
