namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// `lt::HSVMap` (0x28 B) as `SoftISP::Stats+0x80` holds it: `nh`/`ns`/`nv` divisions and a
/// `(nh+1)·(ns+1)` grid of `vec4x32f` cells `(Δh + 1, satScale, valScale, 0)` — the very grid
/// <see cref="ColorFit.Grid"/> already builds bit-identically (`FUN_18017b500`, spec `a-huesat-exact.md`).
/// Only `color_correction = optimized` (`setColorCorrection` lambda_60 `180419ed0`) fills it; `default` leaves it zero.
/// Spec `a-display-isp.md` §8.4.
/// </summary>
public sealed class HsvMap
{
    public int Nh { get; init; }
    public int Ns { get; init; }
    public int Nv { get; init; }
    /// <summary>`(nh+1)·(ns+1)` cells × 4 floats, cell `[i + (nh+1)·j]` (i = hue, j = saturation).</summary>
    public float[] Cells { get; init; } = Array.Empty<float>();

    /// <summary>`HSVMap::isEmpty()` `18017bd10`: `(nh &lt;= 0) | (ns &lt;= 0) | (nv &lt;= 0)`.</summary>
    public bool IsEmpty => Nh <= 0 || Ns <= 0 || Nv <= 0;

    public static HsvMap Empty => new() { Nh = 0, Ns = 0, Nv = 0, Cells = Array.Empty<float>() };

    public static HsvMap FromGrid(float[] grid) =>
        new() { Nh = ColorFit.HueDivisions, Ns = ColorFit.SatDivisions, Nv = ColorFit.ValDivisions, Cells = grid };

    /// <summary>
    /// `FUN_18041eff0(profile, out, Stats+0xc)` — the CCT interpolation of the two profile HueSatMaps.
    /// Both maps null → empty. `!(cct &gt;= T1)` (so NaN too) → map1; `!(cct &lt;= T2)` → map2; else
    /// `rB = 1f/T2`, `rA = 1f/T1`, `num = (1.0 / (double)cct) − (double)rB`, `den = rA − rB` (a **float**
    /// subtraction), `w = (float)(num / (double)den)` — deliberately NOT `FUN_1800d13d0`'s `rcpps`+Newton
    /// arithmetic — then `FUN_18017ba60`: `out[k] = (map1[k]·w) + (map2[k]·(1 − w))`.
    /// </summary>
    public static HsvMap Interpolate(HsvMap? map1, HsvMap? map2, float t1, float t2, float cct)
    {
        if (map1 is null || map2 is null) return Empty;
        if (!(cct >= t1)) return map1;
        if (!(cct <= t2)) return map2;
        float rB = 1.0f / t2, rA = 1.0f / t1;
        double num = 1.0 / (double)cct;
        num -= (double)rB;
        float den = rA - rB;
        float w = (float)(num / (double)den);
        if (map1.Nh != map2.Nh || map1.Ns != map2.Ns || map1.Nv != map2.Nv) throw new InvalidOperationException("HSVMap dimensions must match");
        float inv = 1.0f - w;
        var cells = new float[map2.Cells.Length];
        for (int k = 0; k < cells.Length; k++) cells[k] = (map1.Cells[k] * w) + (map2.Cells[k] * inv);
        return new HsvMap { Nh = map2.Nh, Ns = map2.Ns, Nv = map2.Nv, Cells = cells };
    }
}
