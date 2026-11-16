using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `ToneMapping:acr` / `light_v1` / `light_v1_lowlight` / `light_v2` — slot 15 (`setToneMapping` enums 3–6 →
/// `new TMO_ACR(enum − 3)`, color body → `TMO_ACR::process` → tile kernel, meta 1/1/1). Per 256×256 tile: `exp2f(ev_offset)`
/// once, then per pixel a DNG `dng_function_exposure_ramp`
/// (white 1, black 0.005, radius 0.0025), three truncating LUT lookups with the fraction taken against the **clamped**
/// index and no output clamp, and a hue-preserving `RGBTone` whose middle channel is rebuilt with a raw `rcpss`
/// (no Newton step). Lane 3 is the untouched source alpha. Finally, unless the output `ColorSpace` equals linear
/// ProPhoto D50 field-for-field, the tile is converted ProPhoto → `output.color_space`.
///
/// <para>Two Lux keys change the stage. Lumen's tuning does not have them. When they are absent, the stage gives Lumen's result.
/// <see cref="HighlightRecoveryKey"/> replaces the tone table with a curve that has a shoulder (<see cref="HighlightRecovery"/>) and
/// desaturates the brightest tones toward white. <see cref="GamutCompressKey"/> compresses colors outside the output color space to
/// its boundary (<see cref="GamutCompression"/>), where Lumen clips each channel.</para>
/// </summary>
public sealed class ToneMappingAcrStage : IStage
{
    /// <summary>The tuning key of the highlight recovery amount, 0 to 100 (<see cref="HighlightRecovery.Curve"/>). Absent or 0 = Lumen's
    /// tone curve.</summary>
    public const string HighlightRecoveryKey = "tone_mapping.highlight_recovery";
    /// <summary>The tuning key that turns on the gamut compression (a value other than 0). Absent or 0 = Lumen's clip.</summary>
    public const string GamutCompressKey = "tone_mapping.gamut_compress";

    readonly string _type; readonly int _mode;
    public ToneMappingAcrStage(string type) { _type = type; _mode = StageEnums.Enum("tone_mapping", type) - 3; }
    public StageName Stage => StageName.ToneMapping;
    public string TypeString => _type;
    public StageMeta Meta => new(1, 1, 1f);

    // vector constants (4× broadcast)
    static readonly float ToeOffset = BitConverter.Int32BitsToSingle(unchecked((int)0xBB23D70B));   // −0.0025000001769512892
    static readonly float QScale = BitConverter.Int32BitsToSingle(0x42C90149);                      // 100.50251007080078
    static readonly float LowerKnee = BitConverter.Int32BitsToSingle(0x3B23D70B);                   // 0.0025000001769512892
    static readonly float BlackPoint = BitConverter.Int32BitsToSingle(unchecked((int)0xBBA3D70B));  // −0.005000000353902578
    static readonly float Slope = BitConverter.Int32BitsToSingle(0x3F80A4AA);                       // 1.0050251483917236
    static readonly float UpperKnee = BitConverter.Int32BitsToSingle(0x3BF5C290);                   // 0.007500000298023224
    const float LutScale = 1024.0f;

    public void Apply(IspPayload p)
    {
        var img = p.Rgb ?? throw new InvalidOperationException("ToneMapping needs the RGB working image");
        float ev = 0f; try { ev = (float)p.Context.Tuning.Num("tone_mapping.ev_offset"); } catch (KeyNotFoundException) { }
        var lut = TmoAcrLuts.ForMode(_mode);
        float recovery = 0f; try { recovery = (float)p.Context.Tuning.Num(HighlightRecoveryKey); } catch (KeyNotFoundException) { }
        bool gamut = false; try { gamut = p.Context.Tuning.Num(GamutCompressKey) != 0.0; } catch (KeyNotFoundException) { }
        // With highlight recovery the table is longer (it covers the inputs above Lumen's white), and its entries up to the knee are Lumen's.
        if (recovery > 0f) lut = HighlightRecovery.Curve(lut, recovery);
        float desaturation = recovery > 0f ? Math.Min(recovery, HighlightRecovery.AmountMax) / HighlightRecovery.AmountMax : 0f;
        var from = ColorSpace.ProPhotoD50; var to = p.Stats.OutSpace;
        bool convert = !from.SameAs(to);
        // The 256×256 tiles are independent (pointwise, in place) — spread over threads when few stage runs are in progress; inside a tile
        // eight pixels at a time (Pixel8: the same operations per lane, the RGBTone branches as masks)
        using var running = StageParallel.Enter();
        var tiles = Tiler.Rects(new RectI(0, 0, img.Width, img.Height), 256, 256).ToArray();
        void Tile(int ti)
        {
            var tile = tiles[ti];
            float gain = MuslMath.Exp2f(ev);   // one UCRT exp2f per tile, the same value in all lanes
            for (int y = tile.Y0; y < tile.Y1; y++)
            {
                var row = img.Row(y);
                int x = tile.X0;
                if (Soa8.Supported) x = Pixel8Row(row, tile.X0, tile.X1, gain, lut);
                for (; x < tile.X1; x++) row[x] = Pixel(row[x], gain, lut);
                if (desaturation > 0f) HighlightRecovery.DesaturateRow(row.Slice(tile.X0, tile.X1 - tile.X0), desaturation);
            }
            if (convert || gamut)
            {
                var view = img.View(new RectI(img.Rect.X0 + tile.X0, img.Rect.Y0 + tile.Y0, img.Rect.X0 + tile.X1, img.Rect.Y0 + tile.Y1));
                if (gamut) GamutCompression.ConvertCompressed(view, from, to, 1, convert);
                else ColorSpaceConvert.Convert(view, view, from, to, 1);
            }
        }
        if (tiles.Length > 1 && StageParallel.Spread) Parallel.For(0, tiles.Length, Tile);
        else for (int ti = 0; ti < tiles.Length; ti++) Tile(ti);
    }

    /// <summary><see cref="Curve"/> on eight lanes: f = t·1024, i = (int)f clamped to [0, top], (f − i)·(lut[i+1] − lut[i]) + lut[i].
    /// <paramref name="top"/> is the index of the last table segment (1023 for Lumen's table).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe Vector256<float> Curve8(float* lp, Vector256<float> t, Vector256<int> top)
    {
        var f = t * Vector256.Create(LutScale);
        var i = Vector256.ConvertToInt32(f);
        i = Vector256.Max(i, Vector256<int>.Zero); i = Vector256.Min(i, top);
        var a = Simd.GatherVector256(lp, i, 4); var b = Simd.GatherVector256(lp, i + Vector256.Create(1), 4);
        return ((f - Vector256.ConvertToSingle(i)) * (b - a)) + a;
    }

    /// <summary><see cref="Pixel"/> on eight pixels per step over [x0, x1) of a row; returns the first pixel not done.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    static unsafe int Pixel8Row(Span<Vec4F> row, int x0, int x1, float gain, float[] lut)
    {
        int x = x0;
        ref float rb = ref Unsafe.As<Vec4F, float>(ref MemoryMarshal.GetReference(row));
        var G = Vector256.Create(gain); var toe = Vector256.Create(ToeOffset); var qs = Vector256.Create(QScale); var lk = Vector256.Create(LowerKnee);
        var bp = Vector256.Create(BlackPoint); var sl = Vector256.Create(Slope); var uk = Vector256.Create(UpperKnee); var ls = Vector256.Create(LutScale);
        var zero = Vector256<float>.Zero; var izero = Vector256<int>.Zero; var top = Vector256.Create(lut.Length - 2); var one = Vector256.Create(1);
        fixed (float* lp = lut)
        for (; x + 8 <= x1; x += 8)
        {
            ref float p = ref Unsafe.Add(ref rb, x * 4);
            Soa8.Load(ref p, out var sR, out var sG, out var sB, out var sA);
            Vector256<float> Ramp8(Vector256<float> v)
            {
                var q = ((v + toe) * (v + toe)) * qs;
                q = Vector256.ConditionalSelect(Vector256.LessThanOrEqual(v, lk), zero, q);
                return Vector256.ConditionalSelect(Vector256.LessThanOrEqual(uk, v), (v + bp) * sl, q);
            }
            var tr = Ramp8(sR * G); var tg = Ramp8(sG * G); var tb = Ramp8(sB * G);
            var R = Curve8(lp, tr, top); var Gc = Curve8(lp, tg, top); var B = Curve8(lp, tb, top);
            // RGBTone: the six orderings as masks; the rebuilt middle channel M = ((rcp(hi − lo)·(mid − lo))·(C_hi − C_lo)) + C_lo
            var gr = Vector256.GreaterThan(tg, tr); var br = Vector256.GreaterThan(tb, tr); var bg = Vector256.GreaterThan(tb, tg); var gb = Vector256.GreaterThan(tg, tb);
            var c1 = gr & br & bg; var c2 = gr & br & ~bg; var c3 = gr & ~br;
            var c4 = ~gr & gb; var c5 = ~gr & ~gb & br; var c6 = ~gr & ~gb & ~br & bg; var c7 = ~gr & ~gb & ~br & ~bg;
            Vector256<float> Sel(Vector256<float> v1, Vector256<float> v2, Vector256<float> v3, Vector256<float> v4, Vector256<float> v5, Vector256<float> v6)
                => Vector256.ConditionalSelect(c1, v1, Vector256.ConditionalSelect(c2, v2, Vector256.ConditionalSelect(c3, v3, Vector256.ConditionalSelect(c4, v4, Vector256.ConditionalSelect(c5, v5, v6)))));
            var thi = Sel(tb, tg, tg, tr, tb, tr); var tlo = Sel(tr, tr, tb, tb, tg, tg); var tmid = Sel(tg, tb, tr, tg, tr, tb);
            var chi = Sel(B, Gc, Gc, R, B, R); var clo = Sel(R, R, B, B, Gc, Gc);
            var M = ((IntelApprox.Reciprocal(thi - tlo) * (tmid - tlo)) * (chi - clo)) + clo;
            var outR = Vector256.ConditionalSelect(c3 | c5, M, R);
            var outG = Vector256.ConditionalSelect(c1 | c4, M, Gc);
            var outB = Vector256.ConditionalSelect(c2 | c6, M, Vector256.ConditionalSelect(c7, Gc, B));
            Soa8.Store(ref p, outR, outG, outB, sA);
        }
        return x;
    }

    static float Rcp(float x) => IntelApprox.ReciprocalScalar(Vector128.CreateScalar(x)).ToScalar();

    /// <summary>The exposure ramp: `t = (0.0075 ≤ v) ? (v − 0.005)·slope : (v ≤ 0.0025) ? 0 : ((v − 0.0025)²)·qScale`
    /// — the square is `(v+c)·(v+c)` then `·qScale`, never re-associated. `cmpleps` is ordered, so NaN takes neither mask.</summary>
    static float Ramp(float v)
    {
        float q = ((v + ToeOffset) * (v + ToeOffset)) * QScale;
        if (v <= LowerKnee) q = 0.0f;
        if (UpperKnee <= v) q = (v + BlackPoint) * Slope;
        return q;
    }

    /// <summary>Scalar LUT lookup: `f = t·1024`, `i = cvttss2si(f)` (truncate), clamp to [0, 1023],
    /// `out = (f − (float)i)·(lut[i+1] − lut[i]) + lut[i]` — the fraction is taken against the CLAMPED index,
    /// so `t &gt; 1` extrapolates along the last segment. The last segment of Lumen's table has the index 1023. A curve with highlight
    /// recovery is longer (<see cref="HighlightRecovery.Curve"/>).</summary>
    static float Curve(float t, float[] lut)
    {
        float f = t * LutScale;
        int i = (int)f, top = lut.Length - 2;
        if (i < 0) i = 0;
        if (i > top) i = top;
        float a = lut[i], b = lut[i + 1];
        float d = b - a;
        float fr = f - (float)i;
        return (fr * d) + a;
    }

    public static Vec4F Pixel(Vec4F s, float gain, float[] lut)
    {
        float vr = s.R * gain, vg = s.G * gain, vb = s.B * gain;
        float tr = Ramp(vr), tg = Ramp(vg), tb = Ramp(vb);
        float R = Curve(tr, lut), G = Curve(tg, lut), B = Curve(tb, lut);
        // RGBTone: the sort keys are the RAMP outputs; the middle channel is rebuilt from the
        // two extremes with a raw rcpss. A NaN comparand takes the "less-than" branch.
        if (tg > tr)
        {
            if (tb > tr)
            {
                if (tb > tg) G = ((Rcp(tb - tr) * (tg - tr)) * (B - R)) + R;   // r < g < b
                else B = ((Rcp(tg - tr) * (tb - tr)) * (G - R)) + R;           // r < b <= g
            }
            else R = ((Rcp(tg - tb) * (tr - tb)) * (G - B)) + B;               // b <= r < g
        }
        else if (tg > tb) G = ((Rcp(tr - tb) * (tg - tb)) * (R - B)) + B;      // b < g <= r
        else if (tb > tr) R = ((Rcp(tb - tg) * (tr - tg)) * (B - G)) + G;      // g <= r < b
        else if (tb > tg) B = ((Rcp(tr - tg) * (tb - tg)) * (R - G)) + G;      // g < b <= r
        else B = G;                                                             // g == b (the ramp maps ≤0.0025 to exactly 0)
        return new Vec4F(R, G, B, s.A);
    }
}
