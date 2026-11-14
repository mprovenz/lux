using Lux.Engine.Diagnostics;

namespace Lux.Library.Depth;

/// <summary>Phase timings of the depth backend (tests): the engine's <see cref="PhaseLog"/>.</summary>
internal static class DepthProfile
{
    internal static bool On { get => PhaseLog.On; set => PhaseLog.On = value; }
    internal static void Clear() => PhaseLog.Clear();
    internal static double Now => PhaseLog.Now;
    internal static PhaseLog.Scope Time(string label) => PhaseLog.Time(label);
    internal static void Add(string label, double at, double ms) => PhaseLog.Add(label, at, ms);
}
