using System.Security.Cryptography;

namespace Lux.Store;

/// <summary>A place to remember a file's content hash against (path, size, modification time), so that opening a capture does not
/// hash 160 MB every time. The library database of the desktop app implements it; without one the hash is computed on each use.</summary>
public interface IContentHashCache
{
    bool TryGet(string fullPath, long size, DateTime modifiedUtc, out string hashHex);
    void Put(string fullPath, long size, DateTime modifiedUtc, string hashHex);
}

/// <summary>
/// One capture's context data — and the one resolver every reader and writer of it goes through (the app, the library, <c>lux-light</c>).
/// <code>
///   &lt;folder with the .lri files&gt;/
///     LuxImageContext/
///       edits/&lt;sha-256 of the .lri&gt;.luxedits     depth edits, settings, rating — user work, never removed by any clean-up
///       cache/&lt;sha-256&gt;.luxcache                 regenerable analysis data (may be deleted at any time)
///   &lt;local context store&gt;/&lt;sha-256&gt;/edits/…, cache/…   the same, for a capture whose folder cannot be written
/// </code>
/// Everything is addressed by the SHA-256 of the capture file's content, never by its name: a renamed capture finds its data again,
/// and another file that takes over an old name does not pick up data that is not its own.
/// <para><b>Resolution</b> (per file): reading looks next to the capture first, then in the local store (<see cref="LocalContextStore"/>;
/// <see cref="LocalContextStore.Current"/> unless another is given). A file that does not exist yet is created next to the capture when the
/// capture's folder can be written (a real write probe, cached per folder), else in the local store. Without a local store this is exactly
/// the earlier behavior (next to the capture only). Nothing here hashes the capture when no context data can exist for it.</para>
/// </summary>
public sealed class ImageContext
{
    public const string FolderName = "LuxImageContext", EditsFolderName = "edits", CacheFolderName = "cache", EditsExtension = ".luxedits";

    /// <summary>The capture file.</summary>
    public string CapturePath { get; }
    /// <summary>The <c>LuxImageContext</c> folder beside the capture (it may not exist).</summary>
    public string BesideRoot { get; }
    /// <summary>The local store consulted (null = only next to the capture).</summary>
    public LocalContextStore? Store { get; }
    readonly IContentHashCache? _hashes; string? _hash;

    /// <param name="store">The local context store; null = <see cref="LocalContextStore.Current"/> unless <paramref name="useLocalStore"/> is false.</param>
    public ImageContext(string capturePath, IContentHashCache? hashes = null, LocalContextStore? store = null, bool useLocalStore = true)
    {
        CapturePath = Path.GetFullPath(capturePath); _hashes = hashes;
        BesideRoot = Path.Combine(Path.GetDirectoryName(CapturePath) ?? ".", FolderName);
        Store = useLocalStore ? store ?? LocalContextStore.Current : null;
    }

    /// <summary>A context for a capture whose content hash the caller already knows (the library, which verified it when it scanned).</summary>
    public static ImageContext ForKnownCapture(string capturePath, string contentHash, IContentHashCache? hashes = null, LocalContextStore? store = null)
        => new(capturePath, hashes, store) { _hash = contentHash };

    // ------------------------------------------------------------------------------------------------ resolution

    bool LocalMayHold => Store is not null && Store.MayHoldAny;

    /// <summary>The capture's folder in the local store (hashes the capture); null without a store.</summary>
    public string? LocalRoot => Store?.RootFor(ContentHash);

    /// <summary>Whether context data may exist for this capture at all — cheap (no hashing): a <c>LuxImageContext</c> folder beside it, or a
    /// non-empty local store.</summary>
    public bool MayHaveContext => Directory.Exists(BesideRoot) || LocalMayHold;

    /// <summary>Where new context data of this capture goes: next to it when its folder can be written (or when there is no local store),
    /// else the local store.</summary>
    public string WriteRoot
    {
        get
        {
            if (Store is null) return BesideRoot;
            string probe = Directory.Exists(BesideRoot) ? BesideRoot : Path.GetDirectoryName(CapturePath) ?? ".";
            return LocalContextStore.IsWritable(probe) ? BesideRoot : Store.RootFor(ContentHash);
        }
    }

    /// <summary>The context folder that exists for this capture (beside first, then local), or null — no write probe.</summary>
    public string? ExistingRoot
        => Directory.Exists(BesideRoot) ? BesideRoot : LocalMayHold && Directory.Exists(Store!.RootFor(ContentHash)) ? Store.RootFor(ContentHash) : null;

    /// <summary>The context folder: <see cref="ExistingRoot"/>, else <see cref="WriteRoot"/>.</summary>
    public string Root => ExistingRoot ?? WriteRoot;

    string? Existing(string sub, string ext)
    {
        if (!MayHaveContext) return null;
        string b = Path.Combine(BesideRoot, sub, ContentHash + ext);
        if (File.Exists(b)) return b;
        if (LocalMayHold) { string l = Path.Combine(Store!.RootFor(ContentHash), sub, ContentHash + ext); if (File.Exists(l)) return l; }
        return null;
    }

    /// <summary>The existing edit database (beside first, then local), or null.</summary>
    public string? ExistingEditsPath => Directory.Exists(Path.Combine(BesideRoot, EditsFolderName)) || LocalMayHold ? Existing(EditsFolderName, EditsExtension) : null;
    /// <summary>The existing analysis cache file (beside first, then local), or null.</summary>
    public string? ExistingCachePath => Existing(CacheFolderName, AnalysisCacheStore.Extension);

    /// <summary>Where the capture's context lives: next to it, in the local store, or nowhere yet (by its edit database, else its cache).</summary>
    public ContextPlace Place
    {
        get
        {
            var p = ExistingEditsPath ?? ExistingCachePath;
            if (p is null) return ContextPlace.None;
            return p.StartsWith(BesideRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? ContextPlace.Beside : ContextPlace.Local;
        }
    }

    /// <summary>The folder of the edit database: where it exists, else where it would be created.</summary>
    public string EditsFolder => Path.GetDirectoryName(EditsDatabasePath)!;
    /// <summary>The folder of the analysis cache: where it exists, else where it would be created.</summary>
    public string CacheFolder => ExistingCachePath is { } p ? Path.GetDirectoryName(p)! : Path.Combine(WriteRoot, CacheFolderName);

    /// <summary>Whether any edit database can exist for this capture. When not, no capture here has saved edits and nothing needs to be
    /// hashed.</summary>
    public bool HasEditsFolder => Directory.Exists(Path.Combine(BesideRoot, EditsFolderName)) || LocalMayHold;

    /// <summary>Size of the capture file in bytes.</summary>
    public long CaptureBytes => Target().Length;

    /// <summary>The file itself when the capture path is a link to it (size and modification time are the file's, not the link's).</summary>
    FileInfo Target()
    {
        var fi = new FileInfo(CapturePath);
        return fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo t ? t : fi;
    }

    /// <summary>SHA-256 of the capture file's content, lower-case hex. Computed once per instance (or taken from the hash cache).</summary>
    public string ContentHash
    {
        get
        {
            if (_hash != null) return _hash;
            var fi = Target();
            if (_hashes != null && _hashes.TryGet(CapturePath, fi.Length, fi.LastWriteTimeUtc, out var cached)) return _hash = cached;
            _hash = HashFile(CapturePath);
            _hashes?.Put(CapturePath, fi.Length, fi.LastWriteTimeUtc, _hash);
            return _hash;
        }
    }

    /// <summary>The edit database of this capture: the existing one (beside first, then local), else where it would be created.</summary>
    public string EditsDatabasePath => ExistingEditsPath ?? Path.Combine(WriteRoot, EditsFolderName, ContentHash + EditsExtension);

    /// <summary>Whether this capture has an edit database (in either place). Hashes the capture only when one can exist.</summary>
    public bool HasEdits => ExistingEditsPath is not null;

    /// <summary>Create the <c>edits</c> and <c>cache</c> folders where new context data of this capture goes (<see cref="WriteRoot"/>).</summary>
    public void EnsureFolders()
    {
        string root = WriteRoot;
        Directory.CreateDirectory(Path.Combine(root, EditsFolderName));
        Directory.CreateDirectory(Path.Combine(root, CacheFolderName));
        if (Store is not null && root != BesideRoot) Store.Touched();
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
