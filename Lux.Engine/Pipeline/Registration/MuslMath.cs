namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// Alias of the CRT functions for existing callers. Every function forwards to <see cref="WineCrt"/>, which states the verified domains.
/// <para>Wine's ucrtbase has musl's argument reduction for <c>Sinf</c> / <c>Cosf</c>, but different (Taylor) kernels, one ulp apart on
/// many arguments. On the arguments that the callers reach — the Lanczos-3 tap tables (764 sinf arguments) and the ResAmp Hann windows of
/// the L16 (N = 40 and 34) — musl and Wine agree bit for bit. Musl differs on 11 other window lengths (26, 28, 33, 41, 42, 43,
/// 46, 49, 50, 54, 58).</para>
/// </summary>
public static class MuslMath
{
    /// <summary><see cref="WineCrt.Sinf"/>.</summary>
    public static float Sinf(float x) => WineCrt.Sinf(x);
    /// <summary><see cref="WineCrt.Cosf"/>.</summary>
    public static float Cosf(float x) => WineCrt.Cosf(x);
    /// <summary><see cref="WineCrt.Powf"/>.</summary>
    public static float Powf(float x, float y) => WineCrt.Powf(x, y);
    /// <summary><see cref="WineCrt.Exp2f"/>.</summary>
    public static float Exp2f(float x) => WineCrt.Exp2f(x);
    /// <summary><see cref="WineCrt.Expf"/>.</summary>
    public static float Expf(float x) => WineCrt.Expf(x);
    /// <summary><see cref="WineCrt.Logf"/>.</summary>
    public static float Logf(float x) => WineCrt.Logf(x);
}
