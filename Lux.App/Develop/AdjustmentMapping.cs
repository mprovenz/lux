using Lux.Engine.Pipeline;

namespace Lux.App.Develop;

/// <summary>The page's adjustment values as the engine's <see cref="Adjustments"/> (the export settings of what is on screen).</summary>
public static class AdjustmentMapping
{
    public static Adjustments ToEngine(IReadOnlyDictionary<string, double> values, IReadOnlyList<AdjustmentSpec> specs)
    {
        float V(string key) => (float)(values.TryGetValue(key, out var v) ? v : specs.First(s => s.Key == key).Default);
        bool Moved(string key) => specs.FirstOrDefault(s => s.Key == key) is { } s && values.TryGetValue(key, out var v) && Math.Abs(v - s.Default) > 1e-9;
        return new Adjustments
        {
            Exposure = V(AdjustmentKeys.Exposure),
            Contrast = V(AdjustmentKeys.Contrast),
            Temperature = Moved(AdjustmentKeys.Temperature) ? V(AdjustmentKeys.Temperature) : null,
            Tint = Moved(AdjustmentKeys.Tint) ? V(AdjustmentKeys.Tint) : null,
            Vibrance = V(AdjustmentKeys.Vibrance),
            Saturation = V(AdjustmentKeys.Saturation),
            Sharpening = V(AdjustmentKeys.Sharpening),
            Shadows = V(AdjustmentKeys.Shadows),
            Highlights = V(AdjustmentKeys.Highlights),
            Clarity = V(AdjustmentKeys.Clarity),
        };
    }
}
