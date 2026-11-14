namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// The depth editor's exact CRT `cosf` (musl's argument reduction, Taylor kernels). It forwards to <see cref="WineCrt.Cosf"/>,
/// which is exact on every float below 2^28·π/2.
/// </summary>
public static class EditCrt
{
    /// <summary><see cref="WineCrt.Cosf"/>.</summary>
    public static float Cosf(float x) => WineCrt.Cosf(x);
}
