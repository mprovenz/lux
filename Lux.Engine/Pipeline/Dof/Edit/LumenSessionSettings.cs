using System.Buffers.Binary;
using Google.Protobuf;
using Lux.Engine.Pipeline.Export;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// The fourteen renderer settings (`ParamFloat` 0–13) of a Lumen session's RendererState record, exactly as
/// Lumen loads them. The load starts from the defaults (<see cref="Defaults"/>), copies
/// every field of the `ltpb::Settings` message — <b>set or not</b>: an absent field is the message default, 0, and an absent DOF
/// sub-message gives f-number 0 and focus 0 — then clamps (SSE `maxss`/`minss`, so a NaN lands on the bound the instruction returns):
/// <code>
///   focus (1)        only when 0 &lt; v (or NaN): [100, 100000]        f-number (0)   [1, 1000]          exposure (2)  [−5, 5]
///   temperature (3)  [2000, 20000]                                tint (4)       [−150, 150]
///   contrast, saturation, vibrance, clarity (7–10)  [−100, 100] (`maxps`/`minps`)        blacks, whites (11, 12)  [−100, 100]
///   shadows, highlights (5, 6), sharpening (13): not clamped
/// </code>
/// The RendererState component keeps the result, and after the whole file has been read the renderer takes it (the
/// 14 floats and ParamInt 20 = −1 under the settings lock) and pushes it (renderer request 5).
/// </summary>
public sealed class LumenRendererSettings
{
    public const int Count = 14;
    public const int FNumberId = 0, FocusDepthId = 1, ExposureId = 2, TemperatureId = 3, TintId = 4, ShadowsId = 5, HighlightsId = 6, ContrastId = 7,
                     SaturationId = 8, VibranceId = 9, ClarityId = 10, BlacksId = 11, WhitesId = 12, SharpeningId = 13;
    /// <summary>The defaults: {1000, −1, 0, 5000, 0 …}.</summary>
    public static readonly float[] Defaults = { 1000f, -1f, 0f, 5000f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
    public static readonly string[] Names = { "f-number", "focus depth", "exposure", "temperature", "tint", "shadows", "highlights", "contrast",
                                              "saturation", "vibrance", "clarity", "blacks", "whites", "sharpening" };

    /// <summary>The values as the message holds them (an absent field = 0), before the clamps.</summary>
    public float[] Stored { get; init; } = new float[Count];
    /// <summary>The values Lumen loads: <see cref="Stored"/> after the clamps.</summary>
    public float[] Values { get; init; } = new float[Count];
    /// <summary>Which fields the message actually carries (the DOF sub-message counts for ids 0 and 1).</summary>
    public bool[] Present { get; init; } = new bool[Count];

    public float FNumber => Values[FNumberId];
    public float FocusDepth => Values[FocusDepthId];

    /// <summary>The load clamps, in Lumen's operation order.</summary>
    public static LumenRendererSettings Load(Ltpb.Settings m)
    {
        var s = new float[Count];
        bool hasDof = m.Dof != null;
        s[0] = hasDof ? m.Dof!.FNum : 0f; s[1] = hasDof ? m.Dof!.FocusDepth : 0f;
        s[2] = m.Exposure; s[3] = m.ColorTemparature; s[4] = m.ColorTint; s[5] = m.ShadowBoost; s[6] = m.HighlightBoost; s[7] = m.Contrast;
        s[8] = m.Saturation; s[9] = m.Vibrance; s[10] = m.Clarity; s[11] = m.Blacks; s[12] = m.Whites; s[13] = m.Sharpening;
        var present = new[] { hasDof && m.Dof!.HasFNum, hasDof && m.Dof!.HasFocusDepth, m.HasExposure, m.HasColorTemparature, m.HasColorTint, m.HasShadowBoost,
                              m.HasHighlightBoost, m.HasContrast, m.HasSaturation, m.HasVibrance, m.HasClarity, m.HasBlacks, m.HasWhites, m.HasSharpening };
        var v = (float[])s.Clone();
        // clamped when 0 < focus or when focus is NaN
        if (!(0f >= v[1])) v[1] = MinSs(MaxSs(v[1], 100f), 100000f);
        v[0] = MinSs(MaxSs(1f, v[0]), 1000f);                  // operand order: NaN gives 1000
        v[2] = MinSs(MaxSs(-5f, v[2]), 5f);
        v[3] = MinSs(MaxSs(2000f, v[3]), 20000f);
        v[4] = MinSs(MaxSs(-150f, v[4]), 150f);
        for (int i = 7; i <= 10; i++) v[i] = MinSs(MaxSs(v[i], -100f), 100f);   // operand order: NaN gives −100
        v[11] = MinSs(MaxSs(v[11], -100f), 100f);              // operand order: NaN gives −100
        v[12] = MinSs(MaxSs(-100f, v[12]), 100f);              // operand order: NaN gives 100
        return new LumenRendererSettings { Stored = s, Values = v, Present = present };
    }

    /// <summary>SSE `maxss dst, src`: dst &gt; src ? dst : src (the second operand when unordered or equal).</summary>
    static float MaxSs(float dst, float src) => dst > src ? dst : src;
    /// <summary>SSE `minss dst, src`: dst &lt; src ? dst : src.</summary>
    static float MinSs(float dst, float src) => dst < src ? dst : src;

    /// <summary>The tone and colour settings as Lux's <see cref="Pipeline.Adjustments"/>. Temperature and tint are always given: Lumen's
    /// white-balance property mapping runs only when they differ from the capture's loaded pair (float <c>!=</c>), which
    /// <see cref="Pipeline.Adjustments.PushesWhiteBalance"/> decides the same way. Blacks and whites have no mapper in Lumen (unreleased).
    /// No range validation: Lumen applies whatever the load clamps leave (exposure up to ±5, beyond the editor's ±3).</summary>
    public Adjustments ToAdjustments() => new()
    {
        Exposure = Values[ExposureId], Temperature = Values[TemperatureId], Tint = Values[TintId], Shadows = Values[ShadowsId], Highlights = Values[HighlightsId],
        Contrast = Values[ContrastId], Saturation = Values[SaturationId], Vibrance = Values[VibranceId], Clarity = Values[ClarityId], Sharpening = Values[SharpeningId],
    };
}

/// <summary>
/// The RendererState record (id 0x12345678; component `lt::RendererState`):
/// payload = <c>u32 w, u32 h</c>, the thumbnail (w·h RGBA8, the display render of the editor's last level-4 view), then the renderer's
/// `ParamByteArray 0` bytes (the GUI never sets it, so empty); message `ltpb::Renderer { version = 1, settings = 3 }`.
/// The writer always stores version 0.26.3 and the settings only when the component holds them.
/// </summary>
public sealed class LumenRendererState
{
    public LumenSessionRecord Record { get; init; } = null!;
    public Ltpb.Renderer Message { get; init; } = null!;
    public (uint Major, uint Minor, uint Revision) Version { get; init; }
    public int ThumbnailWidth { get; init; }
    public int ThumbnailHeight { get; init; }
    /// <summary>Offset of the thumbnail pixels in the record (w·h·4 bytes); −1 when w·h ≤ 0 (then the previous thumbnail stays).</summary>
    public int ThumbnailOffset { get; init; } = -1;
    /// <summary>Length of the `ParamByteArray 0` bytes stored after the thumbnail.</summary>
    public int ByteArrayLength { get; init; }
    /// <summary>The settings as loaded; null when the message has none (the record then leaves the settings as they were).</summary>
    public LumenRendererSettings? Settings { get; init; }

    public ReadOnlySpan<byte> Thumbnail => ThumbnailOffset < 0 ? default : Record.Bytes.Span.Slice(ThumbnailOffset, ThumbnailWidth * ThumbnailHeight * 4);

    /// <summary>The record reader. Throws <see cref="InvalidDataException"/> where Lumen's reader returns false (the whole load then fails):
    /// the message does not parse or misses a required field, has no version, or its minor version is below 8.</summary>
    public static LumenRendererState Parse(LumenSessionRecord rec)
    {
        if (rec.Id != LumenSessionRecord.RendererStateId) throw new InvalidDataException("Invalid magic ID in renderer record in state file!");
        var b = rec.Bytes.Span;
        if (rec.ProtoOffset < LumenSessionRecord.HeaderBytes + 8) throw new InvalidDataException("the renderer record is too short for its thumbnail header");
        int w = BinaryPrimitives.ReadInt32LittleEndian(b[0x20..]), h = BinaryPrimitives.ReadInt32LittleEndian(b[0x24..]);
        long px = (long)w * h; int thumbOff = -1;
        if (px > 0)
        {
            if (0x28 + px * 4 > rec.ProtoOffset) throw new InvalidDataException("the renderer record's thumbnail runs past its payload");
            thumbOff = 0x28;
        }
        long extra = rec.ProtoOffset - 0x28 - (px > 0 ? px * 4 : 0);   // protoOffset − 0x28 + w·h·(−4), the vector resized to it
        if (extra < 0) throw new InvalidDataException("the renderer record's payload is shorter than its thumbnail");
        Ltpb.Renderer msg;
        try { msg = Ltpb.Renderer.Parser.ParseFrom(rec.Proto.Span); }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException($"the renderer record's message does not parse ({e.Message})"); }
        if (!msg.IsInitialized()) throw new InvalidDataException("the renderer record's message misses a required field");
        if (msg.Version == null) throw new InvalidDataException("the renderer record has no version");
        if (msg.Version.Minor < 8) throw new InvalidDataException($"the renderer record's version {msg.Version.Major}.{msg.Version.Minor}.{msg.Version.Revision} is older than this reader (minor 8)");
        return new LumenRendererState
        {
            Record = rec, Message = msg, Version = (msg.Version.Major, msg.Version.Minor, msg.Version.Revision), ThumbnailWidth = w, ThumbnailHeight = h,
            ThumbnailOffset = thumbOff, ByteArrayLength = (int)extra, Settings = msg.Settings == null ? null : LumenRendererSettings.Load(msg.Settings),
        };
    }
}

/// <summary>
/// The Transform record (id 0x12340011; `lt::TransformImpl`): the
/// photo's crop, aspect and rotation/flip matrix — `ltpb::Transform { aspect_ratio_x, aspect_ratio_y, crop_rect (start, size),
/// matrix (3×3) }`, all required. The reader reduces the aspect by its gcd and requires it to equal the renderer's ("Transform domain
/// aspect ratio in protobuf does not match current!"), copies the nine floats verbatim (Lumen's storage order:
/// <c>x' = m0·x + m3·y + m6</c>) and the crop as <c>(x, y, width + x, height + y)</c> (float additions).
/// </summary>
public sealed class LumenTransformState
{
    public LumenSessionRecord Record { get; init; } = null!;
    public int AspectX { get; init; }
    public int AspectY { get; init; }
    /// <summary>The aspect reduced by its gcd; (0, 0) when either is 0.</summary>
    public (int X, int Y) ReducedAspect { get; init; }
    public float StartX { get; init; }
    public float StartY { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float[] Matrix { get; init; } = new float[9];
    /// <summary>The crop as Lumen loads it.</summary>
    public (float X0, float Y0, float X1, float Y1) Crop { get; init; }

    public static LumenTransformState Parse(LumenSessionRecord rec)
    {
        if (rec.Id != LumenSessionRecord.TransformId) throw new InvalidDataException("Invalid magic ID in renderer record in state file!");
        Ltpb.Transform m;
        try { m = Ltpb.Transform.Parser.ParseFrom(rec.Proto.Span); }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException($"the transform record's message does not parse ({e.Message})"); }
        if (!m.IsInitialized() || m.CropRect == null || m.Matrix == null) throw new InvalidDataException("the transform record misses a required field");
        int ax = m.AspectRatioX, ay = m.AspectRatioY; (int, int) red = (0, 0);
        if (ax != 0 && ay != 0) { int g = ExportTransform.Gcd(ax, ay); red = (ax / g, ay / g); }
        var c = m.CropRect; var x = m.Matrix;
        return new LumenTransformState
        {
            Record = rec, AspectX = ax, AspectY = ay, ReducedAspect = red, StartX = c.StartX, StartY = c.StartY, Width = c.Width, Height = c.Height,
            Matrix = new[] { x.M00, x.M01, x.M02, x.M10, x.M11, x.M12, x.M20, x.M21, x.M22 },
            Crop = (c.StartX, c.StartY, c.Width + c.StartX, c.Height + c.StartY),
        };
    }

    /// <summary>The identity transform of a fresh renderer (matrix I, crop (0, 0, 1, 1)).</summary>
    public bool IsIdentity => Crop == (0f, 0f, 1f, 1f) && Matrix.SequenceEqual(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f });

    /// <summary>`CIAPI::Transform::crop()`: (x0, y0, x1 − x0, y1 − y0).</summary>
    public (float X, float Y, float W, float H) CropRect => (Crop.X0, Crop.Y0, Crop.X1 - Crop.X0, Crop.Y1 - Crop.Y0);

    /// <summary>The transform for the export path (`GetExportTransformOutput`), on a capture whose level-0 export dims are <paramref name="dims0"/>.
    /// Throws <see cref="InvalidDataException"/> where Lumen's reader throws: the stored aspect differs from the capture's.</summary>
    public ExportTransform ToExportTransform((int W, int H) dims0)
    {
        var t = ExportTransform.Identity(dims0);
        if (ReducedAspect != (t.AspectW, t.AspectH))
            throw new InvalidDataException($"the session's transform has the aspect ratio {AspectX}:{AspectY}, this capture {t.AspectW}:{t.AspectH} (Transform domain aspect ratio in protobuf does not match current!)");
        t.Matrix = (float[])Matrix.Clone();
        (t.CropX0, t.CropY0, t.CropX1, t.CropY1) = Crop;
        return t;
    }

    /// <summary>Lumen.exe's full-size export size (size mode 2 with level 0): the level's dims times the crop's width and
    /// height, <c>(int)((float)W · crop.w)</c> — so a 90° rotation (crop 0.75 × 1.333…) exports 6240 × 8320 from 8320 × 6240.</summary>
    public (int W, int H) ExportSize((int W, int H) levelDims)
    {
        var (_, _, cw, ch) = CropRect;
        return ((int)((float)levelDims.W * cw), (int)((float)levelDims.H * ch));
    }
}

/// <summary>
/// The MetaData record (id 0xa1b2c3; `lt::MetaData`):
/// `ltpb::MetaData { uuid = 1 (low, high), user_rating = 2 }`. The reader <b>merges</b>: it sets the uuid only when the message has one and
/// the rating only when the message has one. `CIAPI::StateFileEditor::serialize` appends a MetaData record holding only
/// the new rating when the gallery changes a rating without opening the photo — so a file can hold two, and the later rating wins.
/// After the load the renderer requires the uuid and checks it against the capture's image UUID ("Metadata UUID in state file doens't
/// match image UUID!", renderer request 5).
/// </summary>
public sealed class LumenMetaDataState
{
    public ulong? UuidLow { get; init; }
    public ulong? UuidHigh { get; init; }
    public uint? Rating { get; init; }

    public static Ltpb.MetaData ParseMessage(LumenSessionRecord rec)
    {
        if (rec.Id != LumenSessionRecord.MetaDataId) throw new InvalidDataException("Invalid magic ID in Metadata record in state file!");
        try { return Ltpb.MetaData.Parser.ParseFrom(rec.Proto.Span); }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException($"the metadata record's message does not parse ({e.Message})"); }
    }

    /// <summary>The MetaData reader applied to the state so far.</summary>
    public LumenMetaDataState Merge(Ltpb.MetaData m) => new()
    {
        UuidLow = m.Uuid != null ? m.Uuid.Low : UuidLow, UuidHigh = m.Uuid != null ? m.Uuid.High : UuidHigh,
        Rating = m.HasUserRating ? m.UserRating : Rating,
    };
}

/// <summary>
/// Everything a Lumen session stores besides the depth: the renderer settings (f-number, focus, the tone and colour sliders, sharpening),
/// the transform (crop / rotation / flips), the rating and the image UUID — the result of reading every RendererState, Transform and
/// MetaData record of the file in order, with Lumen's own reader semantics (a later record replaces the earlier settings / transform;
/// MetaData merges). These three components ignore the StateType: they are read and written by every save type.
/// </summary>
public sealed class LumenSessionSettings
{
    /// <summary>The last RendererState record that carried settings (null = the file has none: the renderer keeps its own).</summary>
    public LumenRendererState? Renderer { get; init; }
    /// <summary>Every RendererState record, in file order.</summary>
    public IReadOnlyList<LumenRendererState> RendererRecords { get; init; } = Array.Empty<LumenRendererState>();
    /// <summary>The last Transform record (null = none: the renderer keeps its identity transform).</summary>
    public LumenTransformState? Transform { get; init; }
    public int TransformRecords { get; init; }
    public LumenMetaDataState MetaData { get; init; } = new();
    public int MetaDataRecords { get; init; }

    public LumenRendererSettings? Values => Renderer?.Settings;
    public uint? Rating => MetaData.Rating;

    /// <summary>Whether anything differs from what a fresh renderer of the capture holds: given the capture's loaded f-number (its
    /// DefaultFNumber) and white balance, true when any setting differs, the transform is not the identity, or a rating is set.</summary>
    public bool HasNonDefault(float defaultFNumber, float temperature, float tint)
    {
        if (Transform is { IsIdentity: false }) return true;
        if (Rating is > 0) return true;
        if (Values is not { } v) return false;
        if (v.FNumber < defaultFNumber) return true;
        if (v.Values[LumenRendererSettings.TemperatureId] != temperature || v.Values[LumenRendererSettings.TintId] != tint) return true;
        // every tone value's fresh default is 0, except sharpening: a fresh renderer carries 5 (`CIAPI::ApplyTuning(0)` → ParamFloat 13 = 5)
        for (int i = LumenRendererSettings.ExposureId; i < LumenRendererSettings.Count; i++)
            if (i != LumenRendererSettings.TemperatureId && i != LumenRendererSettings.TintId && v.Values[i] != (i == 13 ? Adjustments.GuiSharpening : 0f)) return true;
        return false;
    }

    /// <summary>Read the settings records of <paramref name="s"/> (the record walk with the three components that ignore the StateType).</summary>
    public static LumenSessionSettings Read(LumenSession s)
    {
        LumenRendererState? withSettings = null; var renderers = new List<LumenRendererState>();
        LumenTransformState? tr = null; int ntr = 0; var meta = new LumenMetaDataState(); int nmeta = 0;
        foreach (var r in s.Records)
        {
            switch (r.Id)
            {
                case LumenSessionRecord.RendererStateId:
                {
                    var rs = LumenRendererState.Parse(r); renderers.Add(rs);
                    if (rs.Settings != null) withSettings = rs;
                    break;
                }
                case LumenSessionRecord.TransformId: tr = LumenTransformState.Parse(r); ntr++; break;
                case LumenSessionRecord.MetaDataId: meta = meta.Merge(LumenMetaDataState.ParseMessage(r)); nmeta++; break;
            }
        }
        return new LumenSessionSettings { Renderer = withSettings, RendererRecords = renderers, Transform = tr, TransformRecords = ntr, MetaData = meta, MetaDataRecords = nmeta };
    }
}
