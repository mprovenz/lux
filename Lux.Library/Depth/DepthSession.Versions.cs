using System.Globalization;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Depth;

/// <summary>The photo's depth versions (<see cref="IDepthEdit.Versions"/>): the clean depth, Lux versions, versions imported from Lumen — all
/// kept in the photo's edit database; switching is non-destructive and makes the version the photo's active one (exports use it).</summary>
internal sealed partial class DepthSession
{
    IReadOnlyList<DepthVersion> _versions = new[] { new DepthVersion("clean", "Clean", DepthVersionKind.Clean, true, 0, 0, null, false) };

    public IReadOnlyList<DepthVersion> Versions { get { lock (_lock) return _versions; } }
    public DepthVersion ActiveVersion { get { lock (_lock) return _versions.FirstOrDefault(v => v.Active) ?? _versions[0]; } }

    internal static string VersionName(VariantInfo v, List<VariantInfo> all)
    {
        if (v.Kind == DepthEditStore.KindLumenImport)
        {
            int k = all.Where(x => x.Kind == DepthEditStore.KindLumenImport).ToList().FindIndex(x => x.Id == v.Id);
            return k <= 0 ? "From Lumen" : $"From Lumen {k + 1}";
        }
        int i = all.Where(x => x.Kind != DepthEditStore.KindLumenImport).ToList().FindIndex(x => x.Id == v.Id);
        return i <= 0 ? "Lux edits" : $"Lux edits {i + 1}";
    }

    /// <summary>Read the version list again (worker, after any change).</summary>
    void RefreshVersions()
    {
        var list = new List<DepthVersion>();
        string? active = null;
        try
        {
            var store = _store;
            bool own = store is null && _s.Context.HasEdits;
            if (own) store = DepthEditStore.Open(_s.Context.EditsDatabasePath, _s.Context.ContentHash, readOnly: true);
            try
            {
                if (store is not null)
                {
                    active = store.ActiveVariant;
                    var all = store.Variants();
                    foreach (var v in all)
                    {
                        bool tone = false;
                        if (v.Kind == DepthEditStore.KindLumenImport) try { tone = store.ReadRenderSettings(v.Id)?.ToneV2 ?? false; } catch (DepthEditStoreException) { }
                        DateTime? created = DateTime.TryParse(v.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                        list.Add(new DepthVersion(v.Name, VersionName(v, all), v.Kind == DepthEditStore.KindLumenImport ? DepthVersionKind.LumenImport : DepthVersionKind.Lux,
                                                  v.Name == active, (int)v.Cursor, store.LogCount(v.Id), created, v.History == Lux.Store.HistoryState.Unreliable) { NewerToneStyle = tone });
                    }
                }
            }
            finally { if (own) store!.Dispose(); }
        }
        catch (Exception e) when (e is DepthEditStoreException or IOException) { _s.Lib.Trace("versions: " + e.Message); }
        list.Insert(0, new DepthVersion("clean", "Clean", DepthVersionKind.Clean, active is null, 0, 0, null, false));
        lock (_lock) _versions = list;
    }

    public string? LumenSessionPath
    {
        get
        {
            try { return _s.Lib.GetLumenSession(_s.CaptureId)?.Path; } catch (LibraryException) { return null; }
        }
    }

    public string LumenImportWarning =>
        "Importing brings in the depth edits and settings saved by Lumen for this photo, as a separate version. Lumen keeps its depth map in a "
        + "compact, rounded form, so this version uses Lumen's slightly less precise depth throughout: further depth editing on it is lower "
        + "fidelity than editing again from Lux's own depth. Nothing is lost: the original depth and your Lux versions stay, and you can "
        + "switch back at any time.";

    public DepthJob SwitchVersion(string versionId) => _worker.Enqueue("Switch version", c =>
    {
        if (_ed is null && Status.Availability != DepthAvailability.Ready) return new DepthJobResult(DepthJobOutcome.Failed, "The depth of this photo is not ready yet.");
        if (ActiveVersion.Id == versionId) return new DepthJobResult(DepthJobOutcome.NothingChanged, null);
        if (versionId != "clean" && (_store is null || _store.Find(versionId) is null)) return new DepthJobResult(DepthJobOutcome.Failed, "There is no such version.");
        c.Report("Switching version");
        if (versionId == "clean") { if (_store is not null) _store.ActiveVariant = null; }
        else _store!.ActiveVariant = versionId;
        Reattach(c);
        return new DepthJobResult(DepthJobOutcome.Done, null) { ChangedRect = NormRect.Full };
    }, cancellable: false);

    public DepthJob StartCleanVersion() => _worker.Enqueue("Start a clean version", c =>
    {
        if (_ed is null) return new DepthJobResult(DepthJobOutcome.Failed, "The depth of this photo is not ready yet.");
        c.Report("Starting a clean version");
        _store ??= DepthEditStore.OpenOrCreate(_s.Context);
        _store.ActiveVariant = null;
        Reattach(c);   // the clean depth (Lux's own) on the engine's state
        // a new native variant pinned on it, made active
        _p = DepthEditPersistence.StartClean(_store, _ed!);
        if (PinnedRandom is not null) _p.PinnedRandomDevice = PinnedRandom;
        _variant = _p.Variant;
        ReadHistory(); RefreshVersions(); RaiseHistory(); FlushMirror();
        return new DepthJobResult(DepthJobOutcome.Done, null) { ChangedRect = NormRect.Full };
    }, cancellable: false);

    public DepthJob ImportLumenSession(bool newerToneStyle) => _worker.Enqueue("Import from Lumen", c =>
    {
        var lris = LumenSessionPath;
        if (lris is null) return new DepthJobResult(DepthJobOutcome.Failed, "There is no Lumen session beside this photo.");
        c.Report("Importing the Lumen session");
        try { _p?.Detach(); } catch { }
        _p = null; _store?.Dispose(); _store = null;
        LumenEditImport.Result res;
        try { res = LumenEditImport.Import(_s.Path, lris, _s.Lib.Hashes, newerToneStyle); }
        catch (Exception e) when (e is DepthEditStoreException or InvalidDataException or IOException)
        {
            Reattach(c);
            return new DepthJobResult(DepthJobOutcome.Failed, "The Lumen session could not be imported: " + e.Message);
        }
        _store = DepthEditStore.Open(_s.Context.EditsDatabasePath, _s.Context.ContentHash);
        _store.ActiveVariant = res.Variant;
        Reattach(c);
        return new DepthJobResult(DepthJobOutcome.Done, res.AlreadyImported ? "This Lumen session was imported before; that version is now active." : null) { ChangedRect = NormRect.Full };
    }, cancellable: false);

    /// <summary>The active version changed in the database (worker): the engine forgets the old depth, re-reads the saved settings, the edit
    /// state takes the new base (its session history is dropped), and the new version's editor is attached.</summary>
    void Reattach(DepthWorker.Ctx c)
    {
        DropPendingSilently();
        // renders that read the depth wait for the new version's (WaitForDepth): no tile shows the old depth with the new settings
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _ready = ready;
        _snap = null;
        try { ReattachCore(c); ready.TrySetResult(true); }
        catch { ready.TrySetResult(false); throw; }
    }

    void ReattachCore(DepthWorker.Ctx c)
    {
        using (DepthProfile.Time("switch.flush")) _s.EditImpl.FlushSave();
        if (_st is not null) _s.Engine.ResetDepth(_st);
        try { _p?.Detach(); } catch { }
        _p = null;
        using (DepthProfile.Time("switch.reload")) _s.Engine.ReloadSavedState();
        var (variant, recipe) = SavedDepthEdits.LoadParts(_s.Context);
        using (DepthProfile.Time("switch.rebase")) _s.EditImpl.Rebase(variant, recipe);
        using (DepthProfile.Time("switch.load")) LoadActive(c);
        using (DepthProfile.Time("switch.mirror")) FlushMirror();
        RaiseChanged(DepthChangeKind.Version, NormRect.Full, NormRect.Full);
    }

    void DropPendingSilently()
    {
        lock (_toolLock) { _brushPts = null; _lassoPts.Clear(); _lassoPicture.Clear(); _targetDepth = null; _refPoint = null; _previewMask = null; _selectionMask = null; _selectedPixels = 0; }
    }
}
