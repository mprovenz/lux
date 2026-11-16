using System.Globalization;
using System.Text.Json.Nodes;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;

namespace Lux.Store;

/// <summary>
/// A capture's non-depth edits made in Lux — the Develop page's per-photo recipe (kept in the capture's context
/// database, mirrored in the library). Capture-wide, independent of the depth-edit variants: tone and color (<see cref="Adjustments"/>), the
/// photo transform (Lumen's own crop / rotation / flip matrix, <see cref="Transform"/>) and the depth effect's f-number and focus. Each part is
/// optional; a part that is not set falls back to the active variant's saved settings (an imported Lumen session's), else to the capture's
/// own rendering (<see cref="Merge"/>).
/// <para>JSON with every float as its 32-bit pattern (as the operation log and <see cref="StoredSettings"/> do), so values round-trip bit for
/// bit: <c>{"version":1, "adjustments":{"exposure":u32, "contrast":u32, "temperature":u32|null, "tint":u32|null, "vibrance":u32,
/// "saturation":u32, "sharpening":u32, "shadows":u32, "highlights":u32, "clarity":u32} | null, "transform":{"aspect":[x, y], "crop":[u32 ×4],
/// "matrix":[u32 ×9]} | null, "fnumber":u32|null, "focus":u32|null}</c>. A temperature / tint of null = the photo's own white balance.</para>
/// </summary>
public sealed class CaptureRecipe
{
    public const int Version = 1;
    /// <summary>The whole tone and color state; null = not set here.</summary>
    public Adjustments? Adjustments { get; init; }
    /// <summary>The photo transform; null = not set here.</summary>
    public ExportTransform? Transform { get; init; }
    /// <summary>The depth effect's f-number; null = not set here.</summary>
    public float? FNumber { get; init; }
    /// <summary>The depth effect's focus distance (mm; ≤ 0 = Lumen's default focus); null = not set here.</summary>
    public float? FocusDepth { get; init; }

    /// <summary>The photo's orientation and framing are a decision of its own: set by the first geometry edit (which bakes the held turn
    /// into <see cref="Transform"/>), cleared by "revert to original" / geometry reset. While it is set the display-only "shown as held" turn never
    /// applies — also when the saved transform has come back to the identity. False = explicitly undecided (a reverted imported session whose
    /// own transform would otherwise count as a decision); null = derived (<see cref="IsGeometryDecided"/>). No effect on any render or export.
    /// JSON key <c>geometry_decided</c> (1 / 0), written only when set (earlier recipes keep their bytes).</summary>
    public bool? GeometryDecided { get; init; }

    /// <summary>Nothing is set (nothing to store).</summary>
    public bool IsEmpty => !HasRenderParts && GeometryDecided is null;
    /// <summary>A part that changes renders or exports is set (the marker alone changes none).</summary>
    public bool HasRenderParts => Adjustments is not null || Transform is not null || FNumber is not null || FocusDepth is not null;

    static uint B(float v) => BitConverter.SingleToUInt32Bits(v);
    static float F(JsonNode? n) => BitConverter.UInt32BitsToSingle(n!.GetValue<uint>());
    static JsonNode? Opt(float? v) => v is float f ? JsonValue.Create(B(f)) : null;
    static float? OptF(JsonNode? n) => n is null ? null : F(n);

    public string ToJson()
    {
        var o = new JsonObject { ["version"] = Version };
        if (Adjustments is { } a)
            o["adjustments"] = new JsonObject
            {
                ["exposure"] = B(a.Exposure), ["contrast"] = B(a.Contrast), ["temperature"] = Opt(a.Temperature), ["tint"] = Opt(a.Tint),
                ["vibrance"] = B(a.Vibrance), ["saturation"] = B(a.Saturation), ["sharpening"] = B(a.Sharpening), ["shadows"] = B(a.Shadows),
                ["highlights"] = B(a.Highlights), ["clarity"] = B(a.Clarity),
            };
        else o["adjustments"] = null;
        // The two Lux settings are written only when they are set: a recipe without them keeps its bytes, and a recipe from an earlier
        // version reads as "not set".
        if (Adjustments is { } lux && o["adjustments"] is JsonObject ao)
        {
            if (lux.HighlightRecovery != 0f) ao["highlight_recovery"] = B(lux.HighlightRecovery);
            if (lux.GamutCompress) ao["gamut_compress"] = 1;
        }
        o["transform"] = Transform is { } t
            ? new JsonObject
            {
                ["aspect"] = new JsonArray(t.AspectW, t.AspectH),
                ["crop"] = new JsonArray(B(t.CropX0), B(t.CropY0), B(t.CropX1), B(t.CropY1)),
                ["matrix"] = new JsonArray(t.Matrix.Select(v => (JsonNode)JsonValue.Create(B(v))).ToArray()),
            }
            : null;
        o["fnumber"] = Opt(FNumber);
        o["focus"] = Opt(FocusDepth);
        if (GeometryDecided is bool gd) o["geometry_decided"] = gd ? 1 : 0;
        return o.ToJsonString();
    }

    public static CaptureRecipe FromJson(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        int ver = o["version"]!.GetValue<int>();
        if (ver > Version) throw new InvalidDataException($"the saved edits were written by a newer Lux (recipe version {ver})");
        Adjustments? adj = null;
        if (o["adjustments"] is JsonObject a)
            adj = new Adjustments
            {
                Exposure = F(a["exposure"]), Contrast = F(a["contrast"]), Temperature = OptF(a["temperature"]), Tint = OptF(a["tint"]),
                Vibrance = F(a["vibrance"]), Saturation = F(a["saturation"]), Sharpening = F(a["sharpening"]), Shadows = F(a["shadows"]),
                Highlights = F(a["highlights"]), Clarity = F(a["clarity"]),
                HighlightRecovery = a["highlight_recovery"] is { } hr ? F(hr) : 0f, GamutCompress = a["gamut_compress"] is { } gc && gc.GetValue<int>() != 0,
            };
        ExportTransform? tr = null;
        if (o["transform"] is JsonObject t)
        {
            var asp = t["aspect"]!.AsArray(); var c = t["crop"]!.AsArray(); var m = t["matrix"]!.AsArray();
            if (asp.Count != 2 || c.Count != 4 || m.Count != 9) throw new InvalidDataException("malformed transform");
            tr = new ExportTransform
            {
                AspectW = asp[0]!.GetValue<int>(), AspectH = asp[1]!.GetValue<int>(), CropX0 = F(c[0]), CropY0 = F(c[1]), CropX1 = F(c[2]), CropY1 = F(c[3]),
                Matrix = m.Select(F).ToArray(),
            };
        }
        return new CaptureRecipe { Adjustments = adj, Transform = tr, FNumber = OptF(o["fnumber"]), FocusDepth = OptF(o["focus"]),
                                   GeometryDecided = o["geometry_decided"] is JsonValue gd ? gd.GetValue<int>() != 0 : null };
    }

    /// <summary>
    /// The render state a capture renders with: the recipe's parts over <paramref name="variantState"/> (the active / named variant's saved
    /// settings, null when it has none). Null recipe → <paramref name="variantState"/> unchanged (the earlier behavior exactly). The variant's
    /// session geometry (a Lumen import's own calibration and depth), rating and tone style are kept.
    /// </summary>
    public static SavedRenderState? Merge(SavedRenderState? variantState, CaptureRecipe? recipe)
    {
        if (recipe is null || !recipe.HasRenderParts) return variantState;   // (the geometry marker alone renders nothing)
        var v = variantState;
        return new SavedRenderState
        {
            Variant = v?.Variant ?? "none",
            Source = v is null ? SourceLux : v.Source + "+" + SourceLux,
            Settings = v?.Settings,
            Transform = recipe.Transform ?? v?.Transform,
            Rating = v?.Rating,
            ToneV2 = v?.ToneV2 ?? false,
            SessionGeometry = v?.SessionGeometry,
            FNumber = recipe.FNumber ?? v?.FNumber,
            FocusDepth = recipe.FocusDepth ?? v?.FocusDepth,
            Adjustments = recipe.Adjustments ?? v?.Adjustments,
        };
    }

    /// <summary>The <see cref="SavedRenderState.Source"/> of a recipe ("lux-edits").</summary>
    public const string SourceLux = "lux-edits";

    /// <summary>Whether a capture's geometry is decided (the display-only held turn must not apply): the recipe's marker when it has one, else a non-identity saved
    /// transform of the recipe (a recipe written before the marker existed), or a non-identity transform of the variant (an imported Lumen
    /// session that stored one).</summary>
    public static bool IsGeometryDecided(CaptureRecipe? recipe, ExportTransform? variantTransform)
        => recipe?.GeometryDecided ?? (!IsIdentity(recipe?.Transform) || !IsIdentity(variantTransform));

    public static bool IsIdentity(ExportTransform? t)
        => t is null || (t.CropX0 == 0f && t.CropY0 == 0f && t.CropX1 == 1f && t.CropY1 == 1f && t.Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }));

    /// <summary>One line for messages.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture; var p = new List<string>();
        if (FNumber is float f) p.Add(string.Format(inv, "f/{0:0.##}", f));
        if (FocusDepth is float fd) p.Add(fd > 0f ? string.Format(inv, "focus {0:0} mm", fd) : "default focus");
        if (Adjustments is { } a) p.Add("tone " + a);
        if (Transform is { } t && !(t.CropX0 == 0f && t.CropY0 == 0f && t.CropX1 == 1f && t.CropY1 == 1f && t.Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f })))
            p.Add("crop / rotation");
        return p.Count == 0 ? "nothing" : string.Join(", ", p);
    }
}
