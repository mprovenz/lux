using System.Runtime.CompilerServices;
using static Lux.Engine.Pipeline.Color.LumenColorTables;

namespace Lux.Engine.Pipeline.Color;

/// <summary>`lt::ColorSpace` white point (+0x24: x, y, illuminant enum). `FUN_1800cef60(out, illum)` → `FUN_1800ce600`: xy from `DAT_180687bc0/c00`
/// (valid enums mask `0x1ffd`, "invalid illuminant requested!"); illuminant 0 = `output.white_point native` = (0, 0, 0).</summary>
public readonly record struct WhitePoint(float X, float Y, int Illuminant)
{
    public static WhitePoint Of(int illum)
    {
        if (illum >= 13 || ((0x1ffd >> illum) & 1) == 0) throw new InvalidOperationException("invalid illuminant requested!");
        return new WhitePoint(IlluminantX[illum], IlluminantY[illum], illum);
    }
    /// <summary>The adaptation code compares the illuminant field AS A FLOAT (`param[2] != 0.0`) — the int bits reinterpreted.</summary>
    public float IllumBits => BitConverter.Int32BitsToSingle(Illuminant);
}

/// <summary>
/// `lt::ColorSpace` (0x34 B): +0 the RGB→XYZ matrix (9 floats, row-major), +0x24 the white point, +0x30 the space enum
/// (`output.color_space` table: 0 = custom matrix, 1 none, 2 srgb, 3 adobe_rgb, 4 linear_srgb, 5 linear_prophoto_rgb, 6 linear_adobe_rgb, 7–10 other).
/// Spec `a-reference-guide.md` §3.
/// </summary>
public sealed class ColorSpace
{
    public float[] M = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    public WhitePoint Wp;
    public int Space;

    /// <summary>`FUN_1800cf350`: identity matrix, wp (0, 0, 0), space 1 (none). The Stats ctor default (`1803ddd70`) and `color_correction = none` (lambda_57).</summary>
    public static ColorSpace None() => new() { M = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Wp = new WhitePoint(0f, 0f, 0), Space = 1 };

    /// <summary>`FUN_1800cf380(out, matrix, wp)`: matrix copied verbatim, wp copied, space 0 (custom).</summary>
    public static ColorSpace FromMatrix(ReadOnlySpan<float> m, WhitePoint wp) => new() { M = m.ToArray(), Wp = wp, Space = 0 };

    // primaries (RGB→XYZ, native white): DAT_1806879b4 sRGB (D65), DAT_1806879d8 Adobe RGB (D65), DAT_1806879fc ProPhoto (D50)
    static readonly float[] SrgbPrimaries = Bits(0x3ed32d7c, 0x3eb71437, 0x3e38c49c, 0x3e59c6ed, 0x3f371437, 0x3d93d07d, 0x3c9e6221, 0x3df41aef, 0x3f734721);
    static readonly float[] AdobePrimaries = Bits(0x3f13a4a3, 0x3e3e01de, 0x3e40b39f, 0x3e9841c9, 0x3f2099f3, 0x3d9a294f, 0x3cdd7709, 0x3d90c473, 0x3f7db949);
    static readonly float[] ProPhotoPrimaries = Bits(0x3f4c346c, 0x3e0a6fb1, 0x3d006c6c, 0x3e937a01, 0x3f363d62, 0x38b3b9d6, 0x0, 0x0, 0x3f5340f6);
    /// <summary>`DAT_180687c40[space − 2]`: the native illuminant of each standard space (2..10).</summary>
    static readonly int[] NativeIlluminant = { 7, 7, 7, 5, 7, 0, 0, 0, 7 };
    static float[] Bits(params uint[] b) { var f = new float[b.Length]; for (int i = 0; i < b.Length; i++) f[i] = BitConverter.Int32BitsToSingle(unchecked((int)b[i])); return f; }

    /// <summary>`FUN_1800cef80(out, space, wp, adaptation)`: the standard space `space` adapted to the requested white point:
    /// `M = A · P` with P the primaries and `A = Adaptation(nativeWp(space), wp, type)` (element association `(A[i][2]·P[2][j]) + ((A[i][1]·P[1][j]) + (A[i][0]·P[0][j]))`,
    /// symbolic disassembly 1800cf0ce–1800cf1f6). Space 1 (none) = identity.</summary>
    public static ColorSpace Standard(int space, WhitePoint wp, int adaptation)
    {
        var cs = new ColorSpace { Wp = wp, Space = space };
        if (space == 1) { cs.M = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }; return cs; }
        int native = space - 2 >= 0 && space - 2 < 9 ? NativeIlluminant[space - 2] : 0;
        var nativeWp = WhitePoint.Of(native);
        var A = Adaptation(nativeWp, wp, adaptation);
        float[] P = space switch
        {
            0 => throw new InvalidOperationException("invalid color-space type requested!"),
            2 or 4 => SrgbPrimaries,
            3 or 6 => AdobePrimaries,
            5 => ProPhotoPrimaries,
            _ => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
        };
        var m = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i * 3 + j] = (A[i * 3 + 2] * P[6 + j]) + ((A[i * 3 + 1] * P[3 + j]) + (A[i * 3] * P[j]));
        cs.M = m;
        return cs;
    }

    static ColorSpace? _proPhotoD50;
    /// <summary>`FUN_18038a230` (once-initialised static): `Standard(5, WhitePoint.Of(5), 1)` — linear ProPhoto RGB at D50, the working space of
    /// `color_correction` (target) and of the tone mappers (source).</summary>
    public static ColorSpace ProPhotoD50 => _proPhotoD50 ??= Standard(5, WhitePoint.Of(5), 1);

    /// <summary>The 13-field equality of `setToneMapping::lambda_68` / `LinearTMO::process::lambda_0` (matrix, wp x/y/illum, space; float `ucomiss` compares).</summary>
    public bool SameAs(ColorSpace o)
    {
        if (Wp.Illuminant != o.Wp.Illuminant || Wp.X != o.Wp.X || Wp.Y != o.Wp.Y) return false;
        for (int k = 0; k < 9; k++) if (M[k] != o.M[k]) return false;
        return Space == o.Space;
    }

    // chromatic adaptation cone matrices: DAT_180687948 Bradford, DAT_18068796c CAT02, DAT_180687990 (type 3)
    static readonly float[] Bradford = Bits(0x3f652546, 0x3e886595, 0xbe25460b, 0xbf400d1b, 0x3fdb53f8, 0x3d1652bd, 0x3d1f559b, 0xbd8c49ba, 0x3f83c9ef);
    static readonly float[] Cat02 = Bits(0x3f3b98c8, 0x3edbf488, 0xbe264c30, 0xbf341f21, 0x3fd947ae, 0x3bc7e282, 0x3b449ba6, 0x3c5ed289, 0x3f7bc01a);
    static readonly float[] Sharp3 = Bits(0x3f80ed67, 0x3c3673c5, 0xbc9693c0, 0xbea2d8e4, 0x3fa84474, 0x3b6379b7, 0x0, 0x0, 0x3f800000);

    /// <summary>`FUN_1800ce580(space)`: the native illuminant of a standard space (`DAT_180687c40[space − 2]`, 0 outside 2..10) — what the Pipeline factory
    /// (`1803d8cf0` L263–271) substitutes for `output.white_point = native` (enum 0).</summary>
    public static int NativeIlluminantOf(int space) => space - 2 >= 0 && space - 2 < 9 ? NativeIlluminant[space - 2] : 0;

    /// <summary>`FUN_1800ce6f0(out, wpA, wpB, type)` (symbolic disassembly 1800ce6f0–1800cecda): identity when either illuminant field is 0 (the int read as a float),
    /// when type == 0, or when the two white points are equal (illuminant, y, x); "invalid illuminant white-point!" when an x or y is 0; else with the cone matrix
    /// B (1 Bradford `DAT_180687948`, 2 CAT02 `18068796c`, 3 `DAT_180687990`, other → "unsupported chromatic adaptation type!"):
    /// `invY = 1/y, X = x·invY, Z = ((1 − y) − x)·invY`, cone `c_r = (B[r][2]·Z) + ((B[r][0]·X) + B[r][1])`, `d_r = c_r(B) / c_r(A)`,
    /// `Bi = FUN_1800c2a00(B)`, `G[i][k] = (Bi[i][2]·D[2][k]) + ((Bi[i][1]·D[1][k]) + (Bi[i][0]·D[0][k]))` with D = diag(d),
    /// `out[i][j] = (G[i][2]·B[2][j]) + ((G[i][1]·B[1][j]) + (G[i][0]·B[0][j]))`.</summary>
    public static float[] Adaptation(WhitePoint from, WhitePoint to, int type)
    {
        var I = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        if (from.IllumBits == 0f || to.IllumBits == 0f) return I;
        bool same = from.IllumBits == to.IllumBits && from.Y == to.Y && from.X == to.X;
        if (type == 0 || same) return I;
        if (from.X == 0f || to.X == 0f || from.Y == 0f || to.Y == 0f) throw new InvalidOperationException("invalid illuminant white-point!");
        float[] B = type switch { 3 => Sharp3, 2 => Cat02, 1 => Bradford, _ => throw new InvalidOperationException("unsupported chromatic adaptation type!") };
        float invA = 1.0f / from.Y, xA = from.X * invA, zA = ((1.0f - from.Y) - from.X) * invA;
        float invB = 1.0f / to.Y, xB = to.X * invB, zB = ((1.0f - to.Y) - to.X) * invB;
        var Bi = Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(B);   // FUN_1800c2a00
        var D = new float[9];
        for (int r = 0; r < 3; r++)
        {
            float cA = (B[r * 3 + 2] * zA) + ((B[r * 3] * xA) + B[r * 3 + 1]);
            float cB = (B[r * 3 + 2] * zB) + ((B[r * 3] * xB) + B[r * 3 + 1]);
            D[r * 3 + r] = cB / cA;
        }
        var G = new float[9];
        for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) G[i * 3 + k] = (Bi[i * 3 + 2] * D[6 + k]) + ((Bi[i * 3 + 1] * D[3 + k]) + (Bi[i * 3] * D[k]));
        var o = new float[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[i * 3 + j] = (G[i * 3 + 2] * B[6 + j]) + ((G[i * 3 + 1] * B[3 + j]) + (G[i * 3] * B[j]));
        return o;
    }
}

/// <summary>
/// `lt::ImageConvertColorSpace(dst, src, from, to, adaptation)` (`1800cf3c0`): the (from.space, to.space) kernel from the selector `FUN_1800cf5e0`
/// (linear sources 0/4/5/6/7 → table `0x180687c70`: linear targets `FUN_1800d1720`, sRGB-encoded target (2) `FUN_1800d1da0`), fed the 3×3
/// adaptation `FUN_1800ce6f0(from.wp, to.wp, adaptation)`. Each kernel builds `T = inv(to.M) · A · from.M` in its own float association (symbolic
/// disassembly, spec §3.3), tests `Σ|T − I| &lt; 1e-5` (copy / gamma-only path) and applies the 4×4 `[T 0; 0 1]` per pixel as
/// `((w·c3) + ((z·c2) + (x·c0))) + (y·c1)`.
/// </summary>
public static class ColorSpaceConvert
{
    public const float IdentityEps = 9.999999747378752e-06f;   // DAT_180682620

    /// <summary>`P[i][k] = (inv[i][2]·A[2][k]) + ((inv[i][1]·A[1][k]) + (inv[i][0]·A[0][k]))` with `inv = FUN_1800c2a00(to.M)` (both kernels).</summary>
    static float[] InvTimesA(float[] inv, float[] A)
    {
        var P = new float[9];
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
                P[i * 3 + k] = (inv[i * 3 + 2] * A[6 + k]) + ((inv[i * 3 + 1] * A[3 + k]) + (inv[i * 3] * A[k]));
        return P;
    }

    /// <summary>`T = P · F` in the association of the linear→linear kernel `FUN_1800d1720` (columns as the per-pixel loop consumes them).</summary>
    public static float[] BuildTLinear(ColorSpace from, ColorSpace to, float[] A)
    {
        var P = InvTimesA(Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(to.M), A); var F = from.M;
        float P00 = P[0], P01 = P[1], P02 = P[2], P10 = P[3], P11 = P[4], P12 = P[5], P20 = P[6], P21 = P[7], P22 = P[8];
        float F00 = F[0], F01 = F[1], F02 = F[2], F10 = F[3], F11 = F[4], F12 = F[5], F20 = F[6], F21 = F[7], F22 = F[8];
        return new[]
        {
            ((P01 * F10) + (P00 * F00)) + (F20 * P02),   (P02 * F21) + ((P01 * F11) + (F01 * P00)),   (P02 * F22) + ((P01 * F12) + (F02 * P00)),
            (P12 * F20) + ((P10 * F00) + (F10 * P11)),   ((P11 * F11) + (P10 * F01)) + (F21 * P12),   (P12 * F22) + ((P10 * F02) + (F12 * P11)),
            (F20 * P22) + ((F00 * P20) + (F10 * P21)),   (F21 * P22) + ((P20 * F01) + (F11 * P21)),   (P22 * F22) + ((P21 * F12) + (P20 * F02)),
        };
    }

    /// <summary>`T = P · F` in the association of the linear→sRGB kernel `FUN_1800d1da0`.</summary>
    public static float[] BuildTSrgb(ColorSpace from, ColorSpace to, float[] A)
    {
        var P = InvTimesA(Lux.Engine.Pipeline.Geometry.Mat3F.Inverse(to.M), A); var F = from.M;
        float P00 = P[0], P01 = P[1], P02 = P[2], P10 = P[3], P11 = P[4], P12 = P[5], P20 = P[6], P21 = P[7], P22 = P[8];
        float F00 = F[0], F01 = F[1], F02 = F[2], F10 = F[3], F11 = F[4], F12 = F[5], F20 = F[6], F21 = F[7], F22 = F[8];
        return new[]
        {
            ((P00 * F00) + (F10 * P01)) + (F20 * P02),   (F21 * P02) + ((P00 * F01) + (F11 * P01)),   (F22 * P02) + ((P00 * F02) + (F12 * P01)),
            (P12 * F20) + ((P11 * F10) + (P10 * F00)),   ((P11 * F11) + (P10 * F01)) + (P12 * F21),   (P12 * F22) + ((P11 * F12) + (P10 * F02)),
            (F20 * P22) + ((F10 * P21) + (F00 * P20)),   (F21 * P22) + ((F11 * P21) + (F01 * P20)),   (F22 * P22) + ((P21 * F12) + (F02 * P20)),
        };
    }

    /// <summary>`(((|T02| + |T20|) + (|T01| + |T11 − 1|)) + ((|T10| + |T21|) + (|T00 − 1| + |T12|))) + |T22 − 1|` (both kernels, andps/haddps sum);
    /// the identity path is taken when the sum is NOT ≥ 1e-5 (`jae`: NaN also lands there).</summary>
    public static bool IsIdentity(float[] T)
    {
        float s = (((MathF.Abs(T[2]) + MathF.Abs(T[6])) + (MathF.Abs(T[1]) + MathF.Abs(T[4] + -1.0f))) + ((MathF.Abs(T[3]) + MathF.Abs(T[7])) + (MathF.Abs(T[0] + -1.0f) + MathF.Abs(T[5]))))
                  + MathF.Abs(T[8] + -1.0f);
        return !(s >= IdentityEps);
    }

    /// <summary>The per-pixel 4×4 product: `out.c = ((w·col3) + ((z·col2) + (x·col0))) + (y·col1)` with col3 = (0,0,0,1) and row 3 of the columns 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Apply4(float[] T, float x, float y, float z, float w, out float r, out float g, out float b, out float a)
    {
        r = ((w * 0f) + ((z * T[2]) + (x * T[0]))) + (y * T[1]);
        g = ((w * 0f) + ((z * T[5]) + (x * T[3]))) + (y * T[4]);
        b = ((w * 0f) + ((z * T[8]) + (x * T[6]))) + (y * T[7]);
        a = ((w * 1f) + ((z * 0f) + (x * 0f))) + (y * 0f);
    }

    /// <summary>The sRGB transfer of `FUN_1800d1da0` on one lane: `v = maxps(0, v)`; `m = (bits &amp; 0x7fffff) | 0x3f800000`;
    /// `l = ((((m·0.20420437 + (−1.2525469))·m + 3.3310215)·m + (float((bits + (−4&lt;&lt;23)) &gt;&gt; 23) + (−2.2826788))) · (1/2.4)` clamped to [−126, 128];
    /// `i = trunc(l) + (l &lt; 0 ? −1 : 0)`, `f = l − i`, `p = ((f·0.07802452 + 0.22606716)·f + 0.69583356)·f + 0.9999252`, `pw = bits(p) + (i &lt;&lt; 23)`;
    /// `hi = pw·1.055 + (−0.055)`, `lo = v·12.92`, result `v &lt; 0.0031308 ? lo : hi`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SrgbEncode(float v)
    {
        v = 0f > v ? 0f : v;                                   // maxps(0, v): NaN and −0 pass through
        int bits = BitConverter.SingleToInt32Bits(v);
        float m = BitConverter.Int32BitsToSingle((bits & 0x7fffff) | 0x3f800000);
        float e = (float)((bits + unchecked((int)0xc0800000)) >> 23) + C_M2_2826788;
        float l = ((((m * C_0_2042 + C_M1_2525) * m + C_3_3310) * m) + e) * C_INV24;
        l = l > -126.0f ? l : -126.0f;                          // maxps(l, −126)
        l = l < 128.0f ? l : 128.0f;                            // minps(l, 128)
        int i = (int)l + (BitConverter.SingleToInt32Bits(l) >> 31);
        float f = l - (float)i;
        float p = ((f * C_0_0780 + C_0_2261) * f + C_0_6958) * f + C_0_99993;
        float pw = BitConverter.Int32BitsToSingle((i << 23) + BitConverter.SingleToInt32Bits(p));
        float hi = pw * C_1_055 + C_M0_055;
        float lo = v * C_12_92;
        return v < C_0_0031308 ? lo : hi;
    }
    static readonly float C_0_2042 = BitConverter.Int32BitsToSingle(0x3e511af3), C_M1_2525 = BitConverter.Int32BitsToSingle(unchecked((int)0xbfa05375)),
        C_3_3310 = BitConverter.Int32BitsToSingle(0x40552f75), C_M2_2826788 = BitConverter.Int32BitsToSingle(unchecked((int)0xc0121769)), C_INV24 = BitConverter.Int32BitsToSingle(0x3ed55555),
        C_0_0780 = BitConverter.Int32BitsToSingle(0x3d9fcb52), C_0_2261 = BitConverter.Int32BitsToSingle(0x3e677e26), C_0_6958 = BitConverter.Int32BitsToSingle(0x3f322226),
        C_0_99993 = BitConverter.Int32BitsToSingle(0x3f7ffb19), C_1_055 = BitConverter.Int32BitsToSingle(0x3f870a3d), C_M0_055 = BitConverter.Int32BitsToSingle(unchecked((int)0xbd6147ae)),
        C_12_92 = BitConverter.Int32BitsToSingle(0x414eb852), C_0_0031308 = BitConverter.Int32BitsToSingle(0x3b4d2e1c);

    /// <summary>`ImageConvertColorSpace(dst, src, from, to, adaptation)`; dst may alias src. "empty image data!" on an empty source.</summary>
    public static void Convert(Image<Vec4F> dst, Image<Vec4F> src, ColorSpace from, ColorSpace to, int adaptation)
    {
        if (src.Width < 1 || src.Height < 1) throw new InvalidOperationException("empty image data!");
        if (dst.Width != src.Width || dst.Height != src.Height) throw new ArgumentException("ImageConvertColorSpace: destination size");
        bool linearSource = from.Space is 0 or 4 or 5 or 6 or 7;
        if (!linearSource) throw new NotSupportedException($"ImageConvertColorSpace from space {from.Space} (encoded source kernels 1800d4a10/…): not ported");
        var A = ColorSpace.Adaptation(from.Wp, to.Wp, adaptation);
        int w = src.Width, h = src.Height;
        switch (to.Space)
        {
            case 0 or 1 or 4 or 5 or 6 or 7:
            {   // FUN_1800d1720
                var T = BuildTLinear(from, to, A);
                if (IsIdentity(T)) { if (!ReferenceEquals(dst, src)) for (int y = 0; y < h; y++) src.Row(y).CopyTo(dst.Row(y)); return; }
                for (int y = 0; y < h; y++)
                {
                    var s = src.Row(y); var d = dst.Row(y);
                    for (int x = 0; x < w; x++) { var v = s[x]; Apply4(T, v.R, v.G, v.B, v.A, out float r, out float g, out float b, out float a); d[x] = new Vec4F(r, g, b, a); }
                }
                return;
            }
            case 2:
            {   // FUN_1800d1da0
                var T = BuildTSrgb(from, to, A);
                bool ident = IsIdentity(T);
                for (int y = 0; y < h; y++)
                {
                    var s = src.Row(y); var d = dst.Row(y);
                    for (int x = 0; x < w; x++)
                    {
                        var v = s[x]; float r, g, b, a;
                        if (ident) { r = v.R; g = v.G; b = v.B; a = v.A; }
                        else Apply4(T, v.R, v.G, v.B, v.A, out r, out g, out b, out a);
                        d[x] = new Vec4F(SrgbEncode(r), SrgbEncode(g), SrgbEncode(b), a);
                    }
                }
                return;
            }
            default: throw new NotSupportedException($"ImageConvertColorSpace to space {to.Space} (kernel table 0x180687c70[{to.Space}]): not ported");
        }
    }
}
