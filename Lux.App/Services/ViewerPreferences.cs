using System.Globalization;
using Lux.Library;

namespace Lux.App.Services;

/// <summary>
/// How a changed picture replaces the one on screen (the tile-swap policy of <see cref="Viewer.PhotoViewer"/>): on / off, how long to wait for
/// the whole visible picture before showing it part by part from the centre, and the fade of each part. Kept in the library's settings
/// (<c>viewer.swap</c>, <c>viewer.swap_window_ms</c>, <c>viewer.swap_fade_ms</c>).
/// </summary>
public sealed class ViewerPreferences
{
    public const double DefaultWindowMs = 300, DefaultFadeMs = 120;
    public bool SwapWhole { get; private set; } = true;
    public double WindowMs { get; private set; } = DefaultWindowMs;
    public double FadeMs { get; private set; } = DefaultFadeMs;
    public event Action? Changed;

    public void Load(ILibrarySettings s)
    {
        try
        {
            SwapWhole = s.GetValue("viewer.swap") != "off";
            WindowMs = double.TryParse(s.GetValue("viewer.swap_window_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? Math.Clamp(w, 0, 5000) : DefaultWindowMs;
            FadeMs = double.TryParse(s.GetValue("viewer.swap_fade_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? Math.Clamp(f, 0, 2000) : DefaultFadeMs;
        }
        catch (Exception e) when (e is LibraryException or InvalidOperationException) { }
        Changed?.Invoke();
    }

    /// <summary>Set (values clamped: window 0–5000 ms, fade 0–2000 ms) and save off the UI thread.</summary>
    public void Set(ILibrarySettings s, bool swapWhole, double windowMs, double fadeMs)
    {
        SwapWhole = swapWhole;
        WindowMs = Math.Clamp(windowMs, 0, 5000);
        FadeMs = Math.Clamp(fadeMs, 0, 2000);
        Changed?.Invoke();
        string a = swapWhole ? "on" : "off", b = WindowMs.ToString(CultureInfo.InvariantCulture), c = FadeMs.ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(() =>
        {
            try { s.SetValue("viewer.swap", a); s.SetValue("viewer.swap_window_ms", b); s.SetValue("viewer.swap_fade_ms", c); }
            catch (Exception e) when (e is LibraryException or InvalidOperationException or IOException) { }
        });
    }

    public void ApplyTo(Viewer.PhotoViewer v)
    {
        v.Swap.Enabled = SwapWhole;
        v.Swap.WindowMs = WindowMs;
        v.Swap.FadeMs = FadeMs;
    }
}
