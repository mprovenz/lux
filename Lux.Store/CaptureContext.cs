using Lux.Engine.Session;

namespace Lux.Store;

/// <summary>
/// The engine's view of one capture's <c>LuxImageContext</c> data (<see cref="ICaptureContext"/>): its saved depth edits
/// (<see cref="SavedDepthEdits"/>) and its analysis cache (<see cref="AnalysisCacheStore"/>), both found through the capture's content
/// hash. Creating one touches nothing on disk; the capture is hashed only when something is actually looked up.
/// </summary>
public sealed class StoreCaptureContext : ICaptureContext
{
    public ImageContext Context { get; }
    readonly bool _mayCreateFolder;

    /// <param name="mayCreateFolder">Whether a cache may be started for a capture that has no context folder yet (neither <c>LuxImageContext</c>
    /// beside it nor one in the local context store). Off for `convert`, which never creates one unasked (it adds to a context that exists —
    /// made by the app, by saved edits or by `lux-light cache`); on for `lux-light cache` and the app.</param>
    public StoreCaptureContext(string capturePath, IContentHashCache? hashes = null, bool mayCreateFolder = false)
    {
        Context = new ImageContext(capturePath, hashes); _mayCreateFolder = mayCreateFolder;
    }

    public bool HasSavedEdits => Context.HasEdits;

    public string? ApplySavedEdits(ExportState state, string? variant, Action<string>? log) => SavedDepthEdits.Apply(state, Context, variant, log);

    public SavedRenderState? LoadSavedState(string? variant) => SavedDepthEdits.LoadSavedState(Context, variant);

    public IAnalysisCache? OpenCache(int engineCacheVersion, bool create) =>
        AnalysisCacheStore.Open(Context, engineCacheVersion, create && (_mayCreateFolder || Context.ExistingRoot is not null));
}
