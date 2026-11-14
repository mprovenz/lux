namespace Lux.Engine.Pipeline.Dof;

/// <summary>The depth-effect code's CRT `roundf`. It forwards to <see cref="WineCrt"/>, which holds the other CRT functions.</summary>
public static class DofCrt
{
    /// <summary><see cref="WineCrt.Roundf"/>.</summary>
    public static float Roundf(float x) => WineCrt.Roundf(x);
}
