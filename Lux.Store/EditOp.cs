using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof.Edit;

namespace Lux.Store;

/// <summary>What an operation did when it ran — recorded with it, and expected again when the log is replayed.</summary>
public enum EditOutcome { Done = 0, Nothing = 1, Failed = 2 }

/// <summary>
/// One entry of the operation log: a call on the depth editor with everything needed to make it again.
/// Every float is stored as its 32-bit pattern (an unsigned integer), so no value passes through a decimal text form.
/// <list type="bullet">
/// <item><c>brush</c>: points, radius, depth, adaptive. <c>lasso</c>: points, depth, feather. <c>surface-heal</c>: points, radius.</item>
/// <item><c>edge-heal</c>: points, radius and the in-focus interval (lo, hi) the heal was given — it depends on the depth-effect
/// settings on screen at the time, which are not part of the capture.</item>
/// <item><c>quick-stroke</c>: points, radius, rect (x0, y0, x1, y1), level, positive, and <c>random</c> — the values the stroke drew
/// from the random source, in order. <c>quick-apply</c>: depth. <c>quick-reset</c>, <c>quick-clear</c>.</item>
/// <item><c>undo</c>, <c>redo</c>, <c>reset</c>.</item>
/// <item><c>lumen-import</c>: the edits of a Lumen session were loaded (the session's DepthEditor record is stored beside the log).</item>
/// </list>
/// </summary>
public sealed class EditOp
{
    public const string Brush = "brush", Lasso = "lasso", SurfaceHeal = "surface-heal", EdgeHeal = "edge-heal", QuickStroke = "quick-stroke",
        QuickApply = "quick-apply", QuickReset = "quick-reset", QuickClear = "quick-clear", Undo = "undo", Redo = "redo", Reset = "reset",
        LumenImport = "lumen-import";

    [JsonIgnore] public string Kind { get; set; } = "";
    /// <summary>x0, y0, x1, y1, … as float bit patterns.</summary>
    [JsonPropertyName("points")] public uint[]? Points { get; set; }
    [JsonPropertyName("radius")] public uint? Radius { get; set; }
    [JsonPropertyName("depth")] public uint? Depth { get; set; }
    [JsonPropertyName("feather")] public uint? Feather { get; set; }
    [JsonPropertyName("adaptive")] public bool? Adaptive { get; set; }
    [JsonPropertyName("lo")] public uint? Lo { get; set; }
    [JsonPropertyName("hi")] public uint? Hi { get; set; }
    [JsonPropertyName("rect")] public int[]? Rect { get; set; }
    [JsonPropertyName("level")] public int? Level { get; set; }
    [JsonPropertyName("positive")] public bool? Positive { get; set; }
    [JsonPropertyName("random")] public uint[]? Random { get; set; }
    /// <summary>Free text for entries that describe an origin (the imported session's name and hashes).</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }

    // ---- filled from / for the log row
    [JsonIgnore] public long Seq { get; set; }
    [JsonIgnore] public EditOutcome Outcome { get; set; }
    /// <summary>The error text when <see cref="Outcome"/> is <see cref="EditOutcome.Failed"/>.</summary>
    [JsonIgnore] public string? Message { get; set; }
    /// <summary>The editor's undo cursor after the operation.</summary>
    [JsonIgnore] public long CursorAfter { get; set; }
    /// <summary>Digest of what the operation left behind (the tiles it wrote, the cursor, the edit layer's rect list) — see
    /// <c>DepthEditPersistence.ResultDigest</c>. A replay must reproduce it entry by entry.</summary>
    [JsonIgnore] public byte[] Result { get; set; } = Array.Empty<byte>();
    [JsonIgnore] public string? Utc { get; set; }

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static EditOp FromJson(string kind, string json)
    {
        var o = JsonSerializer.Deserialize<EditOp>(json, Json) ?? throw new InvalidDataException("empty log entry");
        o.Kind = kind; return o;
    }

    public static uint Bits(float v) => BitConverter.SingleToUInt32Bits(v);
    public static float Float(uint? bits) => BitConverter.UInt32BitsToSingle(bits ?? throw new InvalidDataException("a log entry lacks a parameter"));
    public static uint[] PointBits(IReadOnlyList<EditPoint> pts)
    {
        var o = new uint[pts.Count * 2];
        for (int i = 0; i < pts.Count; i++) { o[2 * i] = Bits(pts[i].X); o[2 * i + 1] = Bits(pts[i].Y); }
        return o;
    }
    public List<EditPoint> PointList()
    {
        var p = Points ?? throw new InvalidDataException("a log entry lacks its points");
        var o = new List<EditPoint>(p.Length / 2);
        for (int i = 0; i + 1 < p.Length; i += 2) o.Add(new EditPoint(BitConverter.UInt32BitsToSingle(p[i]), BitConverter.UInt32BitsToSingle(p[i + 1])));
        return o;
    }
    public RectI RectValue() { var r = Rect ?? throw new InvalidDataException("a log entry lacks its rect"); return new RectI(r[0], r[1], r[2], r[3]); }

    /// <summary>The checksum stored with a log row: SHA-256 of "seq \n kind \n params \n outcome \n message \n cursor \n result digest (hex)".</summary>
    public static byte[] RowSha(long seq, string kind, string json, int outcome, string? message, long cursorAfter, byte[] result)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"{seq}\n{kind}\n{json}\n{outcome}\n{message ?? ""}\n{cursorAfter}\n{Convert.ToHexString(result)}"));
}

/// <summary>
/// The random values of a quick-select stroke, derived instead of drawn.
///
/// Lumen seeds the two k-means runs of a stroke from the operating system's random device, so the same stroke gives a different mask
/// each time. Lux derives the values from the capture and the stroke, which makes a stroke repeatable — and still records the values
/// it used in the operation log, so replaying a log never depends on this rule.
/// <code>
///   seed    = SHA-256( "lux-quick-select-v1" ‖ capture SHA-256 (32 bytes) ‖ u32 strokeIndex ‖ i32 level ‖ i32 x0 ‖ i32 y0 ‖ i32 x1 ‖ i32 y1
///                      ‖ u8 positive ‖ u32 pointCount ‖ (u32 bits of x, u32 bits of y) per point )          all integers little-endian
///   value k = the first four bytes, little-endian, of SHA-256( seed ‖ u32 k )                               k = 0, 1, 2, … per device call
/// </code>
/// <c>strokeIndex</c> is the number of quick-select strokes already in the variant's log (the stroke's position in the sequence).
/// </summary>
public static class QuickSelectSeed
{
    public static byte[] Seed(ReadOnlySpan<byte> captureSha256, uint strokeIndex, IReadOnlyList<EditPoint> points, RectI rect, int level, bool positive)
    {
        if (captureSha256.Length != 32) throw new ArgumentException("the capture hash must be 32 bytes");
        var tag = Encoding.ASCII.GetBytes("lux-quick-select-v1");
        var buf = new byte[tag.Length + 32 + 4 + 4 + 16 + 1 + 4 + points.Count * 8]; int p = 0;
        tag.CopyTo(buf, p); p += tag.Length;
        captureSha256.CopyTo(buf.AsSpan(p)); p += 32;
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), v); p += 4; }
        void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p), v); p += 4; }
        U32(strokeIndex); I32(level); I32(rect.X0); I32(rect.Y0); I32(rect.X1); I32(rect.Y1);
        buf[p++] = positive ? (byte)1 : (byte)0;
        U32((uint)points.Count);
        foreach (var pt in points) { U32(BitConverter.SingleToUInt32Bits(pt.X)); U32(BitConverter.SingleToUInt32Bits(pt.Y)); }
        return SHA256.HashData(buf);
    }

    public static uint Value(ReadOnlySpan<byte> seed, uint k)
    {
        Span<byte> b = stackalloc byte[36];
        seed.CopyTo(b); BinaryPrimitives.WriteUInt32LittleEndian(b[32..], k);
        return BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(b));
    }
}
