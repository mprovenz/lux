namespace Lux.Engine.Session;

/// <summary>
/// The persistent data a front end keeps for one capture — its saved depth edits and its analysis cache — behind an interface, so
/// the engine stays free of any storage dependency. <c>Lux.Store</c> implements it over the <c>LuxImageContext</c> folder next to
/// the capture (<c>edits/</c> for user work, <c>cache/</c> for regenerable analysis data), keyed by the capture's content hash.
/// </summary>
/// <remarks>Implementations must be safe to call from any thread; the engine may read and write the cache from render threads.</remarks>
public interface ICaptureContext
{
    /// <summary>Whether the capture has saved depth edits. Must be cheap when it has none (no hashing of the capture).</summary>
    bool HasSavedEdits { get; }

    /// <summary>Apply the capture's saved depth edits — the named <paramref name="variant"/>, or the active one when null — to a built
    /// state (<see cref="ExportState.AttachEdits"/>). Returns a one-line note for the user, or null when nothing was applied. A
    /// variant named explicitly that does not exist is an <see cref="ExportRequestException"/>.</summary>
    string? ApplySavedEdits(ExportState state, string? variant, Action<string>? log);

    /// <summary>The capture's analysis cache for this engine's cache format (<paramref name="engineCacheVersion"/>). A cache written by
    /// another version is stale: it is discarded and, when <paramref name="create"/> is set, started afresh; otherwise null is returned.
    /// Null also when there is no cache and <paramref name="create"/> is false, or it cannot be created (read-only folder).</summary>
    IAnalysisCache? OpenCache(int engineCacheVersion, bool create);

    /// <summary>The render settings saved with the capture's saved-edits variant — the named <paramref name="variant"/>, or the active one when
    /// null — e.g. a Lumen session's settings brought over with its depth edits. Null when the variant carries none (or there is no variant).
    /// A variant named explicitly that does not exist is an <see cref="ExportRequestException"/>.</summary>
    SavedRenderState? LoadSavedState(string? variant) => null;
}

/// <summary>
/// The per-photo render settings a capture's saved-edits variant keeps besides its depth edits — today those of an imported Lumen session
/// (`Pipeline/Dof/Edit/LumenSessionSettings.cs`): the fourteen renderer settings as Lumen loads them (f-number, focus depth, the tone and
/// color settings, sharpening), the photo transform (crop / rotation / flips), the rating, and — for a Lumen session — the session's own
/// registration geometry and depth, which a session render uses in place of the computed ones (<see cref="BuildOptions.SessionRender"/>).
/// </summary>
public sealed class SavedRenderState
{
    public required string Variant { get; init; }
    /// <summary>Where the settings came from ("lumen-session").</summary>
    public required string Source { get; init; }
    /// <summary>`ParamFloat` 0–13 as loaded (<see cref="Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings.Values"/>); null = the session stored
    /// no settings (the renderer keeps the capture's own).</summary>
    public IReadOnlyList<float>? Settings { get; init; }
    /// <summary>The photo transform; null = none (the identity).</summary>
    public Lux.Engine.Pipeline.Export.ExportTransform? Transform { get; init; }
    public uint? Rating { get; init; }
    /// <summary>Lumen's newer tone style for this variant (chosen when the Lumen session was imported: the GUI renderer's v2 tone gate, which a
    /// session does not record): exports apply <see cref="ExportRequest.JpegV2"/> and the <c>light_v2</c> DNG tone curve unless the request
    /// overrides them (<see cref="ExportRequest.OverriddenSettings"/> "tone-v2").</summary>
    public bool ToneV2 { get; init; }
    /// <summary>The session's registration geometry and depth for this capture (a Lumen session render); null = render from the computed ones.</summary>
    public Func<Lux.Engine.Lri.LriFile, CaptureGeometry>? SessionGeometry { get; init; }

    float? _fNumber, _focus; bool _fNumberSet, _focusSet;
    Lux.Engine.Pipeline.Adjustments? _adjustments; bool _adjustmentsSet;

    /// <summary>The f-number (`ParamFloat 0`): <see cref="Settings"/>' unless set explicitly (a Lux recipe, which may leave it unset = null).</summary>
    public float? FNumber { get => _fNumberSet ? _fNumber : Settings?[Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings.FNumberId]; init { _fNumber = value; _fNumberSet = true; } }
    /// <summary>The renderer of the depth effect. A Lumen session and a recipe saved without this setting give Lumen's renderer.</summary>
    public Lux.Engine.Pipeline.Dof.DofRendering DepthEffectRendering { get; init; }
    /// <summary>The focus depth (`ParamFloat 1`, ≤ 0 = Lumen's default focus): <see cref="Settings"/>' unless set explicitly.</summary>
    public float? FocusDepth { get => _focusSet ? _focus : Settings?[Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings.FocusDepthId]; init { _focus = value; _focusSet = true; } }

    /// <summary>The tone and color settings: ids 2–13 of <see cref="Settings"/> as <see cref="Lux.Engine.Pipeline.Adjustments"/> (null without
    /// settings), unless set explicitly (a Lux recipe's own adjustments, whose temperature / tint may be null = the capture's own).</summary>
    public Lux.Engine.Pipeline.Adjustments? Adjustments
    {
        get => _adjustmentsSet ? _adjustments : Settings is null ? null : new Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings { Values = Settings.ToArray() }.ToAdjustments();
        init { _adjustments = value; _adjustmentsSet = true; }
    }

    /// <summary>Whether any render setting is saved (f-number, focus or tone) — what the exports and the viewer then apply.</summary>
    public bool HasRenderSettings => Adjustments is not null || FNumber is not null || FocusDepth is not null;
}

/// <summary>
/// One capture's analysis cache: named blocks grouped in tiers, each stored losslessly with a checksum of its raw bytes. The engine
/// defines what goes in a block (<see cref="AnalysisCache"/>); the store only keeps bytes, verifies them on the way out and reports sizes.
/// </summary>
/// <remarks>Thread-safe. A block that fails its checksum is reported by <see cref="CacheCorruptException"/> and never returned.</remarks>
public interface IAnalysisCache : IDisposable
{
    /// <summary>The block's raw bytes, or null when it is not stored. Throws <see cref="CacheCorruptException"/> when the stored bytes do
    /// not unpack to their checksum.</summary>
    byte[]? Read(string tier, string key);

    /// <summary>The keys stored in a tier.</summary>
    IReadOnlyCollection<string> Keys(string tier);

    /// <summary>Store blocks (replacing any of the same key) in one transaction. <paramref name="elementSize"/> (1, 2 or 4) tells the
    /// compressor how the bytes are grouped (bytes, half floats, 32-bit floats).</summary>
    void Write(string tier, IReadOnlyList<(string Key, byte[] Data)> blocks, int elementSize);

    /// <summary>Record that a tier holds everything it should (written last, after all its blocks), with a small descriptor the engine
    /// checks when it reads the tier back; or clear that mark.</summary>
    void SetComplete(string tier, string? descriptor);

    /// <summary>The descriptor of a complete tier; null when the tier is absent or incomplete.</summary>
    string? CompleteDescriptor(string tier);

    /// <summary>Remove every block of a tier.</summary>
    void Drop(string tier);

    /// <summary>Per tier: block count, raw bytes and stored (compressed) bytes, plus whether it is marked complete.</summary>
    IReadOnlyList<CacheTierStats> Stats();

    /// <summary>Where the cache lives (for messages).</summary>
    string Location { get; }
}

/// <summary>Size of one cache tier.</summary>
public sealed record CacheTierStats(string Tier, int Blocks, long RawBytes, long StoredBytes, bool Complete);

/// <summary>A cache block whose stored bytes do not match their checksum or cannot be unpacked. The engine discards the tier and
/// recomputes it; it never renders from such a block.</summary>
public sealed class CacheCorruptException : Exception
{
    public CacheCorruptException(string message) : base(message) { }
}
