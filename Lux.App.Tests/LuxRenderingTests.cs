using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lux.App.Develop;
using Lux.App.ViewModels;
using Lux.Engine.Pipeline.Dof;
using Lux.Engine.Pipeline.Export;
using Lux.Library;
using Lux.Engine.Pipeline;
using Lux.Engine.Session;
using Lux.Store;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>The Lux rendering of the depth effect on synthetic pictures, through the engine's own builder and blur: the energy of small
/// lights, the highlight expansion, the disc, the grain, and that Lumen's renderer stays the default.</summary>
public sealed class LuxRenderingTests(ITestOutputHelper output)
{
    // a 28 mm module: focal length, 35 mm-equivalent focal length, pixel pitch
    const float F = 3.68f, F35 = 28f, Pitch = 0.0011f;
    static float Nsyn(float fnum) => DofGeometry.SyntheticAperture(new DofConstants(F, F35, 2f, Pitch, 4160, 3120), fnum);

    /// <summary>The depth that gives the signed blur radius <paramref name="r"/> (pixels).</summary>
    static float DepthForRadius(float r, float focus, float fnum)
    {
        float k = F * F / (2f * Nsyn(fnum) * Pitch), rr = r >= 0 ? r : r / 0.5f;
        return focus / (1f - rr * (focus - F) / k);
    }

    static DofImage Run(DofImage colour, DofImage depth, float focus, float fnum, bool post, LuxDofTile? lux, bool synth = true)
    {
        var buf = new DualLayerBuffer();
        var (near, far) = DofModel.InFocusRange(focus, F, Pitch, DofGeometry.BuilderRangeFNumber, DofGeometry.BuilderRangeBlur);
        var c = new DofImage((float[])colour.D.Clone(), 4, colour.W, colour.H);
        if (lux is not null) lux = lux with { Expanded = LuxRendering.Expand(c.D, c.W * c.H, lux) };
        DualLayerBuilder.Build(buf, c, depth, focus, F, F35, Pitch, synth, 0, near, far);
        var rect = new RectI(0, 0, c.W, c.H);
        return new DefocusBlur(buf, rect, rect, F, Pitch, Nsyn(fnum), focus, 0, lux).Run(post);
    }

    static readonly LuxDofTile NoGrain = LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f });

    static (DofImage Colour, DofImage Depth) Flat(int w, int h, float value, float depth)
    {
        var col = new DofImage(w, h, 4); var dep = new DofImage(w, h, 1);
        for (int i = 0; i < w * h; i++) { col.D[i * 4] = col.D[i * 4 + 1] = col.D[i * 4 + 2] = value; col.D[i * 4 + 3] = 1f; dep.D[i] = depth; }
        return (col, dep);
    }

    // ================================================================================================================ energy

    /// <summary>The energy that the blur keeps of a one-pixel light, for the light at several positions: (smallest, largest) ratio.</summary>
    static (double Min, double Max) ImpulseEnergy(float radius, LuxDofTile? lux)
    {
        const float focus = 400f, fnum = 0.5f;
        int sz = Math.Max(160, (int)(4 * radius) + 96); double min = double.MaxValue, max = 0;
        foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (2, 1), (3, 5), (4, 2), (5, 7), (6, 3), (7, 6), (9, 4), (11, 13) })
        {
            var (col, dep) = Flat(sz, sz, 0f, DepthForRadius(radius, focus, fnum));
            int q = ((sz / 2 + dy) * sz + sz / 2 + dx) * 4;
            col.D[q] = col.D[q + 1] = col.D[q + 2] = 0.5f;
            var o = Run(col, dep, focus, fnum, post: false, lux, synth: false);
            double sum = 0; for (int i = 0; i < sz * sz; i++) sum += o.D[i * 4 + 1];
            double ratio = sum / 0.5; min = Math.Min(min, ratio); max = Math.Max(max, ratio);
        }
        return (min, max);
    }

    [Fact]
    public void A_small_light_keeps_its_energy_at_every_position()
    {
        foreach (float r in new[] { 4f, 6f, 8f, 12f, 16f, 24f, 32f, 48f, 64f })
        {
            var lumen = ImpulseEnergy(r, null); var lux = ImpulseEnergy(r, NoGrain);
            output.WriteLine($"radius {r,3}: Lumen {lumen.Min:0.000} … {lumen.Max:0.000}   Lux {lux.Min:0.000} … {lux.Max:0.000}");
            Assert.InRange(lux.Min, 0.97, 1.03);
            Assert.InRange(lux.Max, 0.97, 1.03);
        }
        // Lumen's point-sampled decimation does not: this is the fault that the area filter removes
        var bad = ImpulseEnergy(32f, null);
        Assert.True(bad.Min < 0.5 || bad.Max > 1.5, $"Lumen's renderer keeps {bad.Min:0.00} … {bad.Max:0.00} at radius 32");
    }

    [Fact]
    public void The_area_filter_gives_every_source_pixel_the_same_weight()
    {
        // scale 2.6: the sum of the reduced image times the area of one step is the sum of the source, for an impulse at any position
        const int w = 96, h = 80; double inv = 2.6;
        int dw = (int)(w / inv), dh = (int)(h / inv);
        for (int p = 0; p < 12; p++)
        {
            var src = new DofImage(w, h, 4); int x = 30 + p, y = 25 + (p * 5) % 11;
            src.D[(y * w + x) * 4] = 1f;
            var o = LuxRendering.AreaReduce4(src, dw, dh, 0.0, 0.0, inv, inv);
            double sum = 0; foreach (float v in o.D) sum += v;
            Assert.Equal(1.0, sum * inv * inv, 3);
        }
    }

    // ================================================================================================================ highlights

    /// <summary>The peaks of the discs of 3 × 3 lights at 1, 4 and 16 times display white, on a 0.05 background.</summary>
    static float[] LightPeaks(LuxDofTile? lux, float fnum)
    {
        const int w = 640, h = 240; var (col, dep) = Flat(w, h, 0.05f, 6000f);
        float[] vals = { 1f, 4f, 16f };
        for (int k = 0; k < 3; k++)
            for (int y = 119; y <= 121; y++) for (int x = 79 + k * 160; x <= 81 + k * 160; x++) { int i = (y * w + x) * 4; col.D[i] = col.D[i + 1] = col.D[i + 2] = vals[k]; }
        var o = Run(col, dep, 800f, fnum, post: true, lux);
        return vals.Select((_, k) => o.D[(120 * w + 80 + k * 160) * 4 + 1]).ToArray();
    }

    [Fact]
    public void Lights_above_white_make_bright_discs()
    {
        var lumen = LightPeaks(null, 2f); var lux = LightPeaks(NoGrain, 2f);
        var off = LightPeaks(LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightBoost = 1f }), 2f);
        output.WriteLine($"Lumen {string.Join(" ", lumen.Select(v => v.ToString("0.000")))}   Lux {string.Join(" ", lux.Select(v => v.ToString("0.000")))}   " +
                         $"Lux without expansion {string.Join(" ", off.Select(v => v.ToString("0.000")))}");
        // Lumen's renderer: dim discs (the recorded energy of a clipped light is small)
        Assert.InRange(lumen[0], 0.05f, 0.07f); Assert.InRange(lumen[1], 0.08f, 0.11f); Assert.InRange(lumen[2], 0.17f, 0.25f);
        // the light at white does not change; the lights above white rise with their value, well above the discs of Lumen's renderer
        Assert.InRange(lux[0], off[0] - 0.002f, off[0] + 0.002f);
        Assert.True(lux[1] > 1.5f * lumen[1], $"4 × white: {lux[1]} against {lumen[1]}");
        Assert.True(lux[2] > lux[1] && lux[2] > 2.5f * lumen[2], $"16 × white: {lux[2]} against {lumen[2]}");
    }

    [Fact]
    public void The_expansion_and_its_inverse_agree()
    {
        for (float m = 0f; m < 40f; m += 0.0137f)
        {
            float y = LuxRendering.Expand(m, 8f), back = LuxRendering.Compress(y, 8f);
            if (m <= 1f) { Assert.Equal(m, y); Assert.Equal(m, back); }
            else Assert.InRange(back, m * 0.99999f, m * 1.00001f);
        }
        Assert.Equal(16f, LuxRendering.Expand(2f, 8f), 4);
        float last = 0f;
        for (float m = 0.5f; m < 6f; m += 0.001f) { float y = LuxRendering.Expand(m, 8f); Assert.True(y > last); last = y; }
    }

    [Fact]
    public void The_expansion_starts_at_the_start_value()
    {
        // the curve: no change up to the start, continuous and rising above it, and the inverse agrees
        foreach (float start in new[] { 0.8f, 1f, 1.25f, 2f })
            foreach (float boost in new[] { 1.5f, 2.5f, 4f, 8f })
            {
                float last = 0f;
                for (float m = 0f; m < 40f; m += 0.0137f)
                {
                    float y = LuxRendering.Expand(m, boost, start), back = LuxRendering.Compress(y, boost, start);
                    if (m <= start) { Assert.Equal(m, y); Assert.Equal(m, back); }
                    else { Assert.True(y >= m && y >= last); Assert.InRange(back, m * 0.99999f, m * 1.00001f); }
                    last = y;
                }
                Assert.InRange(LuxRendering.Expand(start + 0.001f, boost, start), start + 0.001f, start + 0.0011f);
            }
        // start 1 is the curve without a start, bit for bit
        for (float m = 0f; m < 40f; m += 0.0137f)
        {
            Assert.Equal(LuxRendering.Expand(m, 8f), LuxRendering.Expand(m, 8f, 1f));
            Assert.Equal(LuxRendering.Compress(m, 8f), LuxRendering.Compress(m, 8f, 1f));
        }
        Assert.Equal(1f, LuxDofOptions.Default.HighlightStart);
        // pixels: a value between white and the start does not change; a value above the start does
        var tile = new LuxDofTile(LuxDofOptions.Default with { HighlightBoost = 4f, HighlightStart = 1.25f }, 2f, 1f, 4f, null);
        float[] px = { 0.6f, 1.2f, 0.3f, 1f, 0.7f, 1.3f, 0.2f, 1f }; var copy = (float[])px.Clone();
        Assert.True(LuxRendering.Expand(px, 2, tile));
        Assert.True(px.AsSpan(0, 4).SequenceEqual(copy.AsSpan(0, 4)));
        float f = LuxRendering.Expand(1.4f, 4f, 1.25f) / 1.4f;
        Assert.Equal(0.7f * f, px[4]); Assert.Equal(1.3f * f, px[5]); Assert.Equal(0.2f * f, px[6]); Assert.Equal(1f, px[7]);
        Assert.False(LuxRendering.Expand(copy, 1, tile));
        // a light between white and the start makes the same disc as without the expansion; a brighter light makes a brighter one
        var none = LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightBoost = 1f });
        var late = LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightBoost = 4f, HighlightStart = 4.5f });
        float[] a = LightPeaks(none, 2f), b = LightPeaks(late, 2f);
        Assert.Equal(a[0], b[0]); Assert.Equal(a[1], b[1]); Assert.True(b[2] > 2f * a[2], $"16 × white: {b[2]} against {a[2]}");
    }

    /// <summary>The color (R, G, B) at the center of the disc of a 3 × 3 light of the color <paramref name="light"/> on a dark background.</summary>
    static float[] DiscColor(LuxDofTile lux, (float R, float G, float B) light)
    {
        const int w = 240, h = 240; var (col, dep) = Flat(w, h, 0.02f, 6000f);
        for (int y = 119; y <= 121; y++) for (int x = 119; x <= 121; x++) { int i = (y * w + x) * 4; col.D[i] = light.R; col.D[i + 1] = light.G; col.D[i + 2] = light.B; }
        var o = Run(col, dep, 800f, 2f, post: true, lux);
        return o.D.AsSpan((120 * w + 120) * 4, 3).ToArray();
    }

    [Fact]
    public void The_purple_guard_keeps_the_cast_of_a_clipped_highlight_out_of_the_disc()
    {
        Assert.True(LuxDofOptions.Default.HighlightPurpleGuard);
        var plain = LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightPurpleGuard = false });
        var guard = LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f });
        // a light above white with green as the smallest channel and a weak color: without the guard its disc is purple, with the guard
        // the disc is neutral and as bright
        var hot = (2.4f, 1.3f, 2.0f);
        float[] p = DiscColor(plain, hot), l = DiscColor(guard, hot);
        output.WriteLine($"purple light: plain {p[0]:0.000} {p[1]:0.000} {p[2]:0.000}   guard {l[0]:0.000} {l[1]:0.000} {l[2]:0.000}");
        Assert.True(p[0] > 1.3f * p[1], "the plain disc has the cast");
        Assert.InRange(l[1] / l[0], 0.97f, 1.03f); Assert.InRange(l[2] / l[0], 0.97f, 1.03f);
        Assert.InRange(l[0] / p[0], 0.9f, 1.1f);
        // lights that the guard does not change: green is not the smallest channel, or the color is strong
        foreach (var light in new[] { (3f, 0.3f, 0.2f), (3f, 2f, 1.2f), (0.4f, 0.3f, 6f), (2.5f, 2.5f, 2.5f), (1.5f, 2f, 3f) })
            Assert.Equal(DiscColor(plain, light), DiscColor(guard, light));
        // the weight: 0 without a green deficit, at a strong color and at the start; 1 for a clear cast well above the start; no step between
        Assert.Equal(0f, LuxRendering.PurpleGuardWeight(2f, 2f, 3f, 3f, 1f));
        Assert.Equal(0f, LuxRendering.PurpleGuardWeight(1f, 0.5f, 3f, 3f, 1f));
        Assert.Equal(0f, LuxRendering.PurpleGuardWeight(0.9f, 0.6f, 1f, 1f, 1f));
        Assert.Equal(1f, LuxRendering.PurpleGuardWeight(2.3f, 1.8f, 3.2f, 3.2f, 1f));
        float last = 0f;
        for (float g = 2f; g > 0f; g -= 0.001f)
        {
            float w = LuxRendering.PurpleGuardWeight(2f, g, 3f, 3f, 1f);
            Assert.InRange(w, 0f, 1f); Assert.InRange(w - last, -0.02f, 0.02f); last = w;
        }
        // pixels with gains: a guarded pixel takes the neutral color of its largest channel, then the factor of the curve
        var tile = new LuxDofTile(LuxDofOptions.Default with { HighlightBoost = 4f, HighlightStart = 1.25f, HighlightPurpleGuard = true }, 2f, 1f, 4f, null);
        float[] px = { 0.6f, 1.2f, 0.3f, 1f, 0.9f, 1.0f, 0.5f, 1f }; var copy = (float[])px.Clone();
        Assert.True(LuxRendering.Expand(px, 2, tile));
        Assert.True(px.AsSpan(0, 4).SequenceEqual(copy.AsSpan(0, 4)));
        float f = LuxRendering.Expand(2f, 4f, 1.25f) / 2f;
        Assert.Equal(1f * f, px[4], 5); Assert.Equal(2f * f, px[5], 5); Assert.Equal(0.5f * f, px[6], 5); Assert.Equal(1f, px[7]);
        // a picture without values above white does not change with the guard
        var (tex, dep) = Texture(400, 300, 1f);
        var on = Run(tex, dep, 900f, 2f, post: true, guard); var off = Run(tex, dep, 900f, 2f, post: true, plain);
        Assert.True(on.D.AsSpan(0, 400 * 300 * 4).SequenceEqual(off.D.AsSpan(0, 400 * 300 * 4)));
    }

    static (DofImage Colour, DofImage Depth) Texture(int w, int h, float scale)
    {
        var col = new DofImage(w, h, 4); var dep = new DofImage(w, h, 1); uint s = 12345;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                float v = ((x / 7 + y / 5) % 2 == 0 ? 0.08f : 0.6f) * scale + (s & 255) * (0.0004f * scale);
                col.D[i * 4] = v * 0.9f; col.D[i * 4 + 1] = v; col.D[i * 4 + 2] = v * 0.7f; col.D[i * 4 + 3] = 1f;
                dep.D[i] = x < w / 2 ? 900f : 5000f + 40f * (y % 50);
            }
        return (col, dep);
    }

    [Fact]
    public void A_picture_without_values_above_white_does_not_change_with_the_expansion()
    {
        var (col, dep) = Texture(400, 300, 1f);
        var on = Run(col, dep, 900f, 2f, post: true, NoGrain);
        var off = Run(col, dep, 900f, 2f, post: true, LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightBoost = 1f }));
        Assert.True(on.D.AsSpan(0, 400 * 300 * 4).SequenceEqual(off.D.AsSpan(0, 400 * 300 * 4)));
        // with values above white the two differ
        var (hot, _) = Texture(400, 300, 3f);
        var on2 = Run(hot, dep, 900f, 2f, post: true, NoGrain);
        var off2 = Run(hot, dep, 900f, 2f, post: true, LuxDofTile.Unit(LuxDofOptions.Default with { Grain = 0f, HighlightBoost = 1f }));
        Assert.False(on2.D.AsSpan(0, 400 * 300 * 4).SequenceEqual(off2.D.AsSpan(0, 400 * 300 * 4)));
    }

    // ================================================================================================================ flat field, edge, ramp

    [Fact]
    public void A_flat_field_stays_flat()
    {
        var (col, dep) = Flat(512, 512, 0.18f, 6000f);
        var o = Run(col, dep, 800f, 2f, post: true, NoGrain);
        for (int i = 0; i < 512 * 512; i++) for (int k = 0; k < 3; k++) Assert.InRange(o.D[i * 4 + k], 0.18f * 0.9999f, 0.18f * 1.0001f);
        // above white too: the expansion and its inverse cancel
        var (hot, _) = Flat(512, 512, 1.7f, 6000f);
        var o2 = Run(hot, dep, 800f, 2f, post: true, NoGrain);
        for (int i = 0; i < 512 * 512; i++) Assert.InRange(o2.D[i * 4 + 1], 1.7f * 0.999f, 1.7f * 1.001f);
    }

    [Fact]
    public void An_occlusion_edge_gets_no_new_artifacts()
    {
        const int w = 640, h = 360; var col = new DofImage(w, h, 4); var dep = new DofImage(w, h, 1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; bool fg = x < 300 + (int)(30 * Math.Sin(y / 40.0)); float v;
                if (fg) { v = (x / 6) % 2 == 0 ? 0.05f : 0.30f; col.D[i * 4] = v * 1.4f; col.D[i * 4 + 1] = v; col.D[i * 4 + 2] = v * 0.5f; dep.D[i] = 800f; }
                else { v = ((x / 10) + (y / 10)) % 2 == 0 ? 0.06f : 0.5f; col.D[i * 4] = v * 0.5f; col.D[i * 4 + 1] = v * 0.8f; col.D[i * 4 + 2] = v * 1.3f; dep.D[i] = 4000f; }
                col.D[i * 4 + 3] = 1f;
            }
        foreach (var (focus, fnum) in new[] { (800f, 2f), (4000f, 2f), (4000f, 1f) })
        {
            var lumen = Run(col, dep, focus, fnum, post: false, null); DefocusBlur.Normalize(lumen);
            var lux = Run(col, dep, focus, fnum, post: true, NoGrain);
            double diff = 0, worst = 0;
            for (int i = 0; i < w * h; i++)
                for (int k = 0; k < 3; k++)
                {
                    float v = lux.D[i * 4 + k];
                    Assert.False(float.IsNaN(v));
                    // a mean of picture values: never outside the range of the picture
                    Assert.InRange(v, 0.05f * 0.5f - 1e-4f, 0.5f * 1.3f + 1e-4f);
                    double d = Math.Abs(v - lumen.D[i * 4 + k]); diff += d; worst = Math.Max(worst, d);
                }
            diff /= w * h * 3;
            output.WriteLine($"focus {focus} mm f/{fnum}: mean |Lux − Lumen| {diff:0.0000}, largest {worst:0.000}");
            // the same layers and the same composite: the two renderers differ only by the kernel (sharper edges of the texture)
            Assert.True(diff < 0.02, $"mean difference {diff}");
        }
    }

    /// <summary>The contrast of a fine checker along a depth ramp, per column.</summary>
    static float[] RampContrast(LuxDofTile? lux, out float[] radius)
    {
        const int w = 900, h = 200; var col = new DofImage(w, h, 4); var dep = new DofImage(w, h, 1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; float v = ((x / 3) + (y / 3)) % 2 == 0 ? 0.08f : 0.4f;
                col.D[i * 4] = col.D[i * 4 + 1] = col.D[i * 4 + 2] = v; col.D[i * 4 + 3] = 1f; dep.D[i] = 1100f + 5900f * x / (w - 1);
            }
        var o = Run(col, dep, 1000f, 2f, post: false, lux); DefocusBlur.Normalize(o);
        radius = DefocusBlur.RadiusMap(dep, 1000f, F, Pitch, Nsyn(2f), DofDevice.For(0));
        var lc = new float[w];
        for (int x = 0; x < w; x++)
        {
            double s = 0, s2 = 0; int n = 0;
            for (int y = 20; y < 180; y++) for (int xx = Math.Max(0, x - 4); xx <= Math.Min(w - 1, x + 4); xx++) { float v = o.D[(y * w + xx) * 4 + 1]; s += v; s2 += v * v; n++; }
            lc[x] = (float)Math.Sqrt(Math.Max(0, s2 / n - s / n * (s / n)));
        }
        return lc;
    }

    [Fact]
    public void A_depth_ramp_keeps_Lumens_radius_steps()
    {
        var lumen = RampContrast(null, out var radius); var lux = RampContrast(NoGrain, out _);
        // inside a band (away from its edges) the blur is one disc: the contrast is constant there, and it drops from band to band
        float lastLumen = float.MaxValue, lastLux = float.MaxValue; int bands = 0;
        foreach (var (lo, hi) in new[] { (2f, 4f), (4f, 8f), (8f, 16f) })
        {
            // the columns of the band's last third (the feather zone of the nearer band is before them)
            var cols = Enumerable.Range(10, 880).Where(x => radius[x] > lo + (hi - lo) * 0.7f && radius[x] < hi - 0.3f).ToArray();
            if (cols.Length < 6) continue;
            bands++;
            float a = cols.Min(x => lumen[x]), b = cols.Max(x => lumen[x]), c = cols.Min(x => lux[x]), d = cols.Max(x => lux[x]);
            output.WriteLine($"band {lo}–{hi} px ({cols.Length} columns): contrast Lumen {a:0.0000} … {b:0.0000}   Lux {c:0.0000} … {d:0.0000}");
            Assert.True(b < lastLumen && d < lastLux, "the contrast must drop from band to band");
            Assert.True(b - a < 0.25f * b + 0.001f && d - c < 0.25f * d + 0.001f, "the contrast must be constant inside a band");
            lastLumen = a; lastLux = c;
        }
        Assert.True(bands >= 2);
    }

    // ================================================================================================================ disc

    [Fact]
    public void The_disc_is_hard_edged_and_has_the_area_of_the_circle()
    {
        foreach (double r in new[] { 1.0, 2.0, 4.0, 6.3, 8.0, 12.0 })
        {
            var k = new LuxRendering.DiscKernel(r);
            // weights: full pixels + partial pixels = 1; the number of pixels (by weight) is the area of the circle
            double sum = 0; int n = 2 * k.R + 1;
            for (int j = 0; j < n; j++) { if (k.Full[j] >= 0) sum += (2 * k.Full[j] + 1) * (double)k.FullW; sum += k.PartW[j].Sum(v => (double)v); }
            Assert.Equal(1.0, sum, 4);
            Assert.Equal(Math.PI * r * r, 1.0 / k.FullW, 0.02 * Math.PI * r * r + 0.05);
        }
        // the picture of one point: flat inside, zero outside, one pixel of transition
        const int sz = 64; var src = new DofImage(sz, sz, 4); src.D[(32 * sz + 32) * 4] = 1f;
        var kern = new LuxRendering.DiscKernel(8.0); var o = LuxRendering.DiscFilter(src, kern);
        for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                double d = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)); float v = o.D[(y * sz + x) * 4];
                if (d < 8 - 0.75) Assert.Equal(kern.FullW, v, 7);
                else if (d > 8 + 0.75) Assert.Equal(0f, v);
                else Assert.InRange(v, 0f, kern.FullW);
            }
    }

    // ================================================================================================================ grain

    /// <summary>A picture with noise of a known amount: standard deviation = <paramref name="k"/>·sqrt(value), shared by the channels to the
    /// part <paramref name="shared"/>. The value rises slowly from left to right. A third of the picture has a strong checker texture.
    /// With <paramref name="flat"/> the picture is one value without texture.</summary>
    static float[] NoisyRect(RectI r, float k, float shared, float? flat = null)
    {
        var px = new float[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                int X = r.X0 + x, Y = r.Y0 + y; float v = flat ?? 0.02f + 0.4f * X / 4160f;
                float texture = flat is null && Y % 300 < 100 ? ((X / 3 + Y / 3) % 2 == 0 ? 0.6f : 1.4f) : 1f;
                float G(uint p)
                {   // twelve uniform values: close to a normal value of unit variance
                    float s = 0; for (uint i = 0; i < 12; i++) { uint h = ((uint)X * 73856093u + (uint)Y) * 19349663u + (p * 12 + i); h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16; s += (h >> 8) / 16777216f; }
                    return s - 6f;
                }
                float sd = k * MathF.Sqrt(v), l = G(0) * MathF.Sqrt(shared), c = MathF.Sqrt(1f - shared); int q = (y * r.Width + x) * 4;
                px[q] = v * texture + sd * (l + c * G(1)); px[q + 1] = v * texture + sd * (l + c * G(2)); px[q + 2] = v * texture + sd * (l + c * G(3)); px[q + 3] = 1f;
            }
        return px;
    }

    static double HighPassStd(float[] px, int w, int h, int channel)
    {
        var k = DofFilters.GaussianKernel(5, 1.2f, 1f); var low = DofFilters.ConvSeparable(px, w, h, 4, k, k);
        double s = 0, s2 = 0; int n = 0;
        for (int y = 8; y < h - 8; y++) for (int x = 8; x < w - 8; x++) { double v = px[(y * w + x) * 4 + channel] - low[(y * w + x) * 4 + channel]; s += v; s2 += v * v; n++; }
        return Math.Sqrt(s2 / n - s / n * (s / n));
    }

    [Fact]
    public void The_grain_model_measures_the_grain_of_the_picture_and_the_added_grain_matches_it()
    {
        const float k = 0.02f;
        var model = LuxNoiseModel.Measure(r => NoisyRect(r, k, 0.5f), (4160, 3120));
        Assert.True(model.Measured);
        output.WriteLine($"shared part {model.Luma * model.Luma:0.00} (0.50 put in)");
        Assert.InRange(model.Luma * model.Luma, 0.35f, 0.65f);
        foreach (float v in new[] { 0.07f, 0.17f, 0.27f, 0.37f })
        {
            // a flat blurred field of this value with grain added: its high-pass part is as strong as the one of the noisy picture
            const int w = 256, h = 256; var img = new DofImage(w, h, 4); var radius = new float[w * h];
            for (int i = 0; i < w * h; i++) { img.D[i * 4] = img.D[i * 4 + 1] = img.D[i * 4 + 2] = v; img.D[i * 4 + 3] = 1f; radius[i] = 20f; }
            LuxRendering.AddGrain(img, radius, _ => 1f, model, 1f, 1000, 2000);
            var noisy = NoisyRect(new RectI(0, 0, w, h), k, 0.5f, v);
            double added = HighPassStd(img.D, w, h, 1), own = HighPassStd(noisy, w, h, 1);
            output.WriteLine($"value {v}: high-pass of the added grain {added:0.00000}, of the picture's noise {own:0.00000}, ratio {added / own:0.00}");
            Assert.InRange(added / own, 0.85, 1.15);
        }
    }

    [Fact]
    public void Grain_is_a_function_of_the_level_position_and_goes_only_where_the_picture_is_blurred()
    {
        var model = LuxNoiseModel.Fallback();
        DofImage Tile(int ox, int oy, int w, int h, Func<int, int, float> radiusAt)
        {
            var img = new DofImage(w, h, 4); var radius = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = y * w + x; img.D[i * 4] = img.D[i * 4 + 1] = img.D[i * 4 + 2] = 0.2f; img.D[i * 4 + 3] = 1f; radius[i] = radiusAt(ox + x, oy + y); }
            LuxRendering.AddGrain(img, radius, r => r >= 1f ? 1f : 0f, model, 1f, ox, oy);
            return img;
        }
        float R(int x, int y) => x < 700 ? 0.3f : 6f;
        var a = Tile(512, 256, 400, 200, R); var b = Tile(640, 300, 300, 150, R); var a2 = Tile(512, 256, 400, 200, R);
        Assert.True(a.D.AsSpan().SequenceEqual(a2.D));                                         // the same on every run
        int grained = 0;
        for (int y = 300; y < 450; y++)
            for (int x = 640; x < 912; x++)
            {
                int ia = ((y - 256) * 400 + (x - 512)) * 4, ib = ((y - 300) * 300 + (x - 640)) * 4;
                for (int c = 0; c < 3; c++) Assert.Equal(BitConverter.SingleToInt32Bits(a.D[ia + c]), BitConverter.SingleToInt32Bits(b.D[ib + c]));   // no seam between tiles
                if (x < 700) Assert.Equal(0.2f, a.D[ia]);                                      // sharp: untouched
                else if (a.D[ia] != 0.2f) grained++;
            }
        Assert.True(grained > 150 * 200 * 0.9);

        // through the renderer: a picture just in front of the focus plane is not blurred and gets no grain
        var (col, dep) = Texture(300, 200, 1f);
        for (int i = 0; i < dep.D.Length; i++) dep.D[i] = DepthForRadius(-0.05f, 1500f, 2f);
        var o = Run(col, dep, 1500f, 2f, post: true, LuxDofTile.Unit(null, model), synth: false);
        for (int i = 0; i < 300 * 200; i++) for (int c = 0; c < 3; c++) Assert.InRange(o.D[i * 4 + c], col.D[i * 4 + c] * 0.99999f, col.D[i * 4 + c] * 1.00001f);
        // behind the focus plane it is blurred and gets grain: two runs give the same bits, and the grain is there
        for (int i = 0; i < dep.D.Length; i++) dep.D[i] = DepthForRadius(12f, 1500f, 2f);
        var (flat, _) = Flat(300, 200, 0.2f, 0f);
        var g1 = Run(flat, dep, 1500f, 2f, post: true, LuxDofTile.Unit(null, model), synth: false);
        var g2 = Run(flat, dep, 1500f, 2f, post: true, LuxDofTile.Unit(null, model), synth: false);
        Assert.True(g1.D.AsSpan(0, 300 * 200 * 4).SequenceEqual(g2.D.AsSpan(0, 300 * 200 * 4)));
        Assert.True(HighPassStd(g1.D, 300, 200, 1) > 0.0005);
    }

    // ================================================================================================================ default and saved edits

    [Fact]
    public void Lumens_renderer_is_the_default()
    {
        Assert.Equal(DofRendering.Lumen, new ExportRequest().DepthEffectRendering);
        Assert.Equal(DofRendering.Lumen, new RenderSettings().DepthEffectRendering);
        Assert.Equal(DofRendering.Lumen, default(DofRendering));
        // the blur without the Lux tile is Lumen's: the same bits as before the option existed, with and without a null argument
        var (col, dep) = Texture(300, 200, 1f);
        var a = Run(col, dep, 900f, 2f, post: true, null);
        var buf = new DualLayerBuffer(); var (near, far) = DofModel.InFocusRange(900f, F, Pitch, DofGeometry.BuilderRangeFNumber, DofGeometry.BuilderRangeBlur);
        DualLayerBuilder.Build(buf, new DofImage((float[])col.D.Clone(), 4, 300, 200), dep, 900f, F, F35, Pitch, true, 0, near, far);
        var rect = new RectI(0, 0, 300, 200);
        var b = new DefocusBlur(buf, rect, rect, F, Pitch, Nsyn(2f), 900f, 0).Run(true);
        Assert.True(a.D.AsSpan(0, 300 * 200 * 4).SequenceEqual(b.D.AsSpan(0, 300 * 200 * 4)));
    }

    [Fact]
    public void Saved_edits_keep_the_rendering_and_old_ones_load_with_Lumens()
    {
        // a recipe written before the setting existed
        const string old = """{"version":1,"adjustments":null,"transform":null,"fnumber":1073741824,"focus":null}""";
        var r = CaptureRecipe.FromJson(old);
        Assert.Equal(DofRendering.Lumen, r.DepthEffectRendering);
        Assert.Equal(2f, r.FNumber);
        // a recipe with Lumen's renderer keeps the old bytes
        Assert.Equal(old, r.ToJson());
        // the Lux rendering is written and read back
        var lux = new CaptureRecipe { FNumber = 2f, DepthEffectRendering = DofRendering.Lux };
        Assert.Contains("\"dof_rendering\":\"lux\"", lux.ToJson());
        Assert.Equal(DofRendering.Lux, CaptureRecipe.FromJson(lux.ToJson()).DepthEffectRendering);
        Assert.Contains("Lux rendering", lux.Describe());
        // it reaches the render state and, from there, an export request that does not set it
        var state = CaptureRecipe.Merge(null, lux)!;
        Assert.Equal(DofRendering.Lux, state.DepthEffectRendering);
        var req = Exporter.WithSavedSettings(new ExportRequest(), state);
        Assert.Equal(DofRendering.Lux, req.DepthEffectRendering); Assert.Equal(2f, req.DepthEffectFNumber);
        var given = Exporter.WithSavedSettings(new ExportRequest { OverriddenSettings = new HashSet<string> { "dof-rendering" } }, state);
        Assert.Equal(DofRendering.Lumen, given.DepthEffectRendering);
        Assert.Equal(DofRendering.Lumen, Exporter.WithSavedSettings(new ExportRequest(), CaptureRecipe.Merge(null, r)!).DepthEffectRendering);
    }

    // ================================================================================================================ a real capture

    static byte[] Render(ExportState st, int level, float? fNumber, DofRendering rendering)
    {
        byte[]? px = null;
        var req = new ExportRequest
        {
            Formats = Array.Empty<ExportImageFormat>(), Level = level, OutDirectory = Paths.UiScratch, Stem = "x", RenderThreads = Environment.ProcessorCount,
            UseSavedEdits = false, CacheUse = CacheUse.None, LensFrames = new LensRequest { Modules = Array.Empty<string>() },
            DepthEffectFNumber = fNumber, FocusDepthMm = fNumber is null ? null : 2000f, DepthEffectNeedsDepth = fNumber is not null, DepthEffectRendering = rendering,
            LuxFormats = (l, r, s, od, sm, outputs, refusals, log) => px = (byte[])s!.Rgba8().Rgba.Clone(),
        };
        Exporter.Run(st, req, null);
        return px!;
    }

    /// <summary>A night capture with city lights at a small size: Lumen's renderer is the default, the Lux rendering gives the same bytes on
    /// every run, makes more of the lights bright, and has no effect without the depth effect.</summary>
    [Fact]
    public void A_real_capture_keeps_Lumens_rendering_by_default_and_the_Lux_rendering_is_repeatable()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00432.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { output.WriteLine("LUX_TEST_CAPTURES has no L16_00432.lri: skipped"); return; }
        Lux.Engine.Pipeline.Color.LumenComponents.EnsureRegistered();
        var st = ExportBuild.Build(lri, 0, null, Environment.ProcessorCount, null, new BuildOptions());
        const int level = 2;
        var sharp = Render(st, level, null, DofRendering.Lumen);
        Assert.Equal(sharp, Render(st, level, null, DofRendering.Lux));                       // no depth effect: the choice changes nothing
        var lumen = Render(st, level, 2f, DofRendering.Lumen);
        Assert.Equal(lumen, Render(st, level, 2f, default));                                  // the default is Lumen's renderer
        Assert.Equal(lumen, Render(st, level, 2f, DofRendering.Lumen));
        var lux = Render(st, level, 2f, DofRendering.Lux);
        Assert.Equal(lux, Render(st, level, 2f, DofRendering.Lux));                           // the same bytes on every run
        int n = lumen.Length / 4, whiteLumen = 0, whiteLux = 0, changed = 0;
        for (int i = 0; i < n; i++)
        {
            if (Math.Max(lumen[i * 4], Math.Max(lumen[i * 4 + 1], lumen[i * 4 + 2])) >= 250) whiteLumen++;
            if (Math.Max(lux[i * 4], Math.Max(lux[i * 4 + 1], lux[i * 4 + 2])) >= 250) whiteLux++;
            if (lumen[i * 4] != lux[i * 4] || lumen[i * 4 + 1] != lux[i * 4 + 1] || lumen[i * 4 + 2] != lux[i * 4 + 2]) changed++;
        }
        output.WriteLine($"white {100.0 * whiteLumen / n:F2} % with Lumen's renderer, {100.0 * whiteLux / n:F2} % with the Lux rendering; {100.0 * changed / n:F1} % of the pixels differ");
        Assert.True(changed > 0.2 * n);
        Assert.True(whiteLux > 1.5 * whiteLumen, "the clipped lights make bright discs");
    }

    // ================================================================================================================ the Develop page (fake)

    [AvaloniaFact]
    public async Task The_rendering_choice_makes_one_step_and_travels_with_the_depth_effect()
    {
        await using var h = await DevelopHarness.OpenAsync();
        var d = h.DepthVm(); var s = h.Session(); var dev = h.Develop();
        Assert.True(d.IsReady);
        Assert.False(d.IsLuxRendering);                                   // Lumen's renderer until chosen
        Assert.False(s.FakeDepth.LuxRendering);
        Button B(string name) => h.DevelopView().GetVisualDescendants().OfType<Button>().First(b => b.Name == name);
        var lumen = B("RenderingLumen"); var lux = B("RenderingLux");
        var note = h.DevelopView().GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RenderingNote");
        Assert.Equal(DepthViewModel.LuxRenderingText, note.Text);
        Assert.Contains("selected", lumen.Classes); Assert.DoesNotContain("selected", lux.Classes);
        // choose Lux: one step, the choice shows
        int steps = s.History.Count;
        lux.Command!.Execute(lux.CommandParameter);
        await h.SettleDepthAsync();
        Assert.True(d.IsLuxRendering); Assert.True(s.FakeDepth.LuxRendering);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.Equal("Lux rendering", s.History[^1]);
        Assert.Equal(HistoryKind.DepthEffect, s.HistoryItems[^1].Kind);
        Assert.Contains("selected", lux.Classes); Assert.DoesNotContain("selected", lumen.Classes);
        // the same choice again: no step
        lux.Command.Execute(lux.CommandParameter);
        await h.SettleDepthAsync();
        Assert.Equal(steps + 1, s.History.Count);
        // the f-number and the focus do not change with it
        Assert.Equal(2.0, s.FakeDepth.FNumber!.Value, 6);
        // undo and redo
        dev.UndoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.False(d.IsLuxRendering);
        dev.RedoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.True(d.IsLuxRendering);
        // copy and paste carry it with the depth effect group
        Assert.True(s.Snapshot().DepthLuxRendering);
        dev.CopyCommand.Execute(null);
        var dlg = Assert.IsType<PatchDialogViewModel>(h.Vm.Dialog);
        dlg.Groups.First(g => g.Group == EditGroups.DepthEffect).IsOn = true;
        await dlg.OkCommand.ExecuteAsync(null);
        await dev.NextCommand.ExecuteAsync(null);
        await h.SettlePictureAsync();
        var s2 = h.Session();
        Assert.False(s2.FakeDepth.LuxRendering);
        dev.PasteCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.True(s2.FakeDepth.LuxRendering);
        Assert.True(h.DepthVm().IsLuxRendering);
        // the section's Reset goes back to Lumen's renderer
        h.DepthVm().ResetEffectCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.False(s2.FakeDepth.LuxRendering);
        Assert.False(h.DepthVm().IsLuxRendering);
        // back to Lumen by the other button
        dev.UndoCommand.Execute(null);
        await h.SettleDepthAsync();
        Assert.True(h.DepthVm().IsLuxRendering);
        var lumen2 = B("RenderingLumen");
        lumen2.Command!.Execute(lumen2.CommandParameter);
        await h.SettleDepthAsync();
        Assert.False(s2.FakeDepth.LuxRendering);
        Assert.Equal("Lumen rendering", s2.History[^1]);
    }

    [Fact]
    public void A_preset_keeps_the_rendering_and_an_old_preset_gives_Lumens()
    {
        var v = new EditValues { Groups = EditGroups.DepthEffect, FNumber = 2f, DepthRendering = DofRendering.Lux };
        string json = Lux.Library.Edit.EditValuesCodec.ToJson(v);
        Assert.Contains("\"dof_rendering\":\"lux\"", json);
        Assert.Equal(DofRendering.Lux, Lux.Library.Edit.EditValuesCodec.FromJson(json).DepthRendering);
        string old = Lux.Library.Edit.EditValuesCodec.ToJson(v with { DepthRendering = DofRendering.Lumen });
        Assert.DoesNotContain("dof_rendering", old);
        Assert.Equal(DofRendering.Lumen, Lux.Library.Edit.EditValuesCodec.FromJson(old).DepthRendering);
    }
}
