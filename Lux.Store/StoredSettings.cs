using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;

namespace Lux.Store;

/// <summary>
/// The per-photo render settings a variant keeps besides its depth edits (table <c>render_settings</c>, schema version 2) — today those of
/// an imported Lumen session (<see cref="LumenSessionSettings"/>): the fourteen renderer settings exactly as Lumen loads them (`ParamFloat`
/// 0–13: f-number, focus depth, exposure, temperature, tint, shadows, highlights, contrast, saturation, vibrance, clarity, blacks, whites,
/// sharpening), the photo transform (aspect, crop as Lumen holds it, the 3×3 matrix), the rating and the image UUID.
/// <para>Stored as JSON with every float as its 32-bit pattern (as the operation log does), so a value round-trips bit for bit:
/// <c>{"settings":[u32 ×14], "present":[0|1 ×14], "transform":{"aspect":[x, y], "crop":[u32 ×4], "matrix":[u32 ×9]} | null,
/// "rating":n | null, "uuid":[low, high] | null, "version":[major, minor, revision] | null}</c>.</para>
/// </summary>
public sealed class StoredSettings
{
    public string Source { get; init; } = SourceLumenSession;
    public const string SourceLumenSession = "lumen-session";
    /// <summary>SHA-256 of the file the settings came from (the <c>.lris</c>).</summary>
    public byte[]? SourceSha { get; init; }
    public float[] Values { get; init; } = (float[])LumenRendererSettings.Defaults.Clone();
    public bool[] Present { get; init; } = new bool[LumenRendererSettings.Count];
    public (int X, int Y)? Aspect { get; init; }
    public (float X0, float Y0, float X1, float Y1)? Crop { get; init; }
    public float[]? Matrix { get; init; }
    public uint? Rating { get; init; }
    public (ulong Low, ulong High)? Uuid { get; init; }
    public (uint Major, uint Minor, uint Revision)? Version { get; init; }
    /// <summary>Lumen's newer tone style (the GUI renderer's v2 tone gate), chosen at import — a session does not record it. JSON key
    /// <c>tone_v2</c>, written only when set (so earlier settings keep their bytes and checksums).</summary>
    public bool ToneV2 { get; init; }

    /// <summary>The settings records of a Lumen session.</summary>
    public static StoredSettings FromSession(LumenSessionSettings s, byte[]? sourceSha)
    {
        var v = s.Values;
        var t = s.Transform;
        return new StoredSettings
        {
            SourceSha = sourceSha,
            Values = v is null ? (float[])LumenRendererSettings.Defaults.Clone() : (float[])v.Values.Clone(),
            Present = v is null ? new bool[LumenRendererSettings.Count] : (bool[])v.Present.Clone(),
            Aspect = t is null ? null : (t.AspectX, t.AspectY), Crop = t?.Crop, Matrix = t is null ? null : (float[])t.Matrix.Clone(),
            Rating = s.MetaData.Rating,
            Uuid = s.MetaData.UuidLow is ulong lo && s.MetaData.UuidHigh is ulong hi ? (lo, hi) : null,
            Version = s.Renderer?.Version,
        };
    }

    /// <summary>Whether the session had a settings message at all (else the values are the renderer defaults, which a session render keeps).</summary>
    public bool HasSettings => Present.Any(p => p) || Version is not null;

    /// <summary>The transform for the export path; null when the session stored none.</summary>
    public ExportTransform? ToExportTransform()
    {
        if (Aspect is not { } a || Crop is not { } c || Matrix is null) return null;
        int ax = a.X, ay = a.Y; if (ax != 0 && ay != 0) { int g = Gcd(ax, ay); ax /= g; ay /= g; }
        return new ExportTransform { Matrix = (float[])Matrix.Clone(), AspectW = ax, AspectH = ay, CropX0 = c.X0, CropY0 = c.Y0, CropX1 = c.X1, CropY1 = c.Y1 };
    }
    static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

    static uint B(float v) => BitConverter.SingleToUInt32Bits(v);
    static float F(JsonNode? n) => BitConverter.UInt32BitsToSingle(n!.GetValue<uint>());

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["settings"] = new JsonArray(Values.Select(v => (JsonNode)JsonValue.Create(B(v))).ToArray()),
            ["present"] = new JsonArray(Present.Select(p => (JsonNode)JsonValue.Create(p ? 1 : 0)).ToArray()),
            ["transform"] = Aspect is { } a && Crop is { } c && Matrix is not null
                ? new JsonObject
                {
                    ["aspect"] = new JsonArray(a.X, a.Y),
                    ["crop"] = new JsonArray(B(c.X0), B(c.Y0), B(c.X1), B(c.Y1)),
                    ["matrix"] = new JsonArray(Matrix.Select(v => (JsonNode)JsonValue.Create(B(v))).ToArray()),
                }
                : null,
            ["rating"] = Rating is uint r ? JsonValue.Create(r) : null,
            ["uuid"] = Uuid is { } u ? new JsonArray(u.Low, u.High) : null,
            ["version"] = Version is { } ver ? new JsonArray(ver.Major, ver.Minor, ver.Revision) : null,
        };
        if (ToneV2) o["tone_v2"] = 1;
        return o.ToJsonString();
    }

    public static StoredSettings FromJson(string source, byte[]? sourceSha, string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var sv = o["settings"]!.AsArray(); var pr = o["present"]!.AsArray();
        if (sv.Count != LumenRendererSettings.Count || pr.Count != LumenRendererSettings.Count) throw new InvalidDataException("render settings: 14 values expected");
        (int, int)? aspect = null; (float, float, float, float)? crop = null; float[]? matrix = null;
        if (o["transform"] is JsonObject t)
        {
            var a = t["aspect"]!.AsArray(); var c = t["crop"]!.AsArray(); var m = t["matrix"]!.AsArray();
            if (a.Count != 2 || c.Count != 4 || m.Count != 9) throw new InvalidDataException("render settings: malformed transform");
            aspect = (a[0]!.GetValue<int>(), a[1]!.GetValue<int>()); crop = (F(c[0]), F(c[1]), F(c[2]), F(c[3])); matrix = m.Select(F).ToArray();
        }
        (ulong, ulong)? uuid = o["uuid"] is JsonArray ua ? (ua[0]!.GetValue<ulong>(), ua[1]!.GetValue<ulong>()) : null;
        (uint, uint, uint)? ver = o["version"] is JsonArray va ? (va[0]!.GetValue<uint>(), va[1]!.GetValue<uint>(), va[2]!.GetValue<uint>()) : null;
        return new StoredSettings
        {
            Source = source, SourceSha = sourceSha, Values = sv.Select(F).ToArray(), Present = pr.Select(p => p!.GetValue<int>() != 0).ToArray(),
            Aspect = aspect, Crop = crop, Matrix = matrix, Rating = o["rating"] is JsonValue rv ? rv.GetValue<uint>() : null, Uuid = uuid, Version = ver,
            ToneV2 = o["tone_v2"] is JsonValue tv && tv.GetValue<int>() != 0,
        };
    }

    /// <summary>One line for messages: the f-number, focus and whatever differs from a neutral setting.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture; var p = new List<string>();
        p.Add(string.Format(inv, "f/{0:0.##}", Values[0]));
        p.Add(Values[1] > 0f ? string.Format(inv, "focus {0:0} mm", Values[1]) : "default focus");
        for (int i = 2; i < LumenRendererSettings.Count; i++)
            if (i != LumenRendererSettings.TemperatureId && i != LumenRendererSettings.TintId && Values[i] != 0f)
                p.Add(string.Format(inv, "{0} {1:0.###}", LumenRendererSettings.Names[i], Values[i]));
        p.Add(string.Format(inv, "white balance {0:0} K / tint {1:0.###}", Values[3], Values[4]));
        if (ToExportTransform() is { } t && !(t.CropX0 == 0f && t.CropY0 == 0f && t.CropX1 == 1f && t.CropY1 == 1f && t.Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f })))
            p.Add("crop / rotation");
        if (Rating is uint r && r > 0) p.Add($"rating {r}");
        if (ToneV2) p.Add("Lumen's newer tone style");
        return string.Join(", ", p);
    }
}
