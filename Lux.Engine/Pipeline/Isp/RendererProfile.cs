namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// `CIAPI::RendererProfile` (0–3), the argument of `CIAPI::Renderer::Create` that selects the module-ISP kernels.
/// Lumen.exe passes 3 at two of its three call sites and `flag ? 3 : 0` at the third (`Lumen.exe 0x14000f8e6`,
/// `0x14003ac8d`, `0x14003bbd7`); "Depth Editor can only be used with a Renderer in Desktop profile!" names it.
/// Getters are the cp.dll profile helpers (`FUN_18050c7f0/c920/cab0/c4b0/c720`, "Invalid Renderer profile!" above 3).
/// </summary>
public enum RendererProfile { Profile0 = 0, Profile1 = 1, Profile2 = 2, Desktop = 3 }

public static class RendererProfiles
{
    public static void Check(RendererProfile p) { if ((int)p > 3 || (int)p < 0) throw new InvalidOperationException("Invalid Renderer profile!"); }
    /// <summary>`FUN_18050c7f0`: profiles 0/2 → "light_v2", 1/3 → "light_v1".</summary>
    public static string DemosaicType(RendererProfile p) { Check(p); return ((int)p & 1) == 0 ? "light_v2" : "light_v1"; }
    /// <summary>`FUN_18050c920`: profiles 0/2 → "bilateral_420", 1/3 → "hybrid".</summary>
    public static string DenoiseType(RendererProfile p) { Check(p); return ((int)p & 1) == 0 ? "bilateral_420" : "hybrid"; }
    /// <summary>`FUN_18050cab0`: profile 0 → "none", 1–3 → "default".</summary>
    public static string ColorNoiseReductionType(RendererProfile p) { Check(p); return p == RendererProfile.Profile0 ? "none" : "default"; }
    /// <summary>`FUN_18050c720`: (9 &gt;&gt; p) &amp; 1 — profiles 0 and 3 ("This profile does not support depth!").</summary>
    public static bool SupportsDepth(RendererProfile p) { Check(p); return ((9 >> (int)p) & 1) != 0; }
    /// <summary>`FUN_18050c4b0`: profile == 3.</summary>
    public static bool IsDesktop(RendererProfile p) { Check(p); return (int)p == 3; }
}
