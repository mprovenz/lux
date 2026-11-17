using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;

namespace Lux.Store;

/// <summary>
/// Saved depth edits in an export: the bridge between a built capture (<see cref="ExportState"/>) and its edit database in the
/// `LuxImageContext` folder next to it (<see cref="ImageContext"/>, <see cref="DepthEditStore"/>).
///
/// A capture with no database is not touched in any way — when the folder `LuxImageContext/edits` does not exist the capture is not
/// even hashed. With a database, the chosen variant's pinned depth replaces the freshly computed depth and its snapshot is put on the
/// depth cache and the edit layer, which every depth reader of the export then uses.
/// </summary>
public static class SavedDepthEdits
{
    /// <summary>The text shown whenever edits imported from Lumen are used or created.</summary>
    public const string LumenImportWarning =
        "Edits imported from Lumen use the depth map Lumen stored in its session file. That depth map is slightly less precise than "
        + "the one Lux computes (Lumen stores it in a compact, rounded form), so results are lower fidelity than editing again from a "
        + "clean Lux start. The clean Lux depth is still there: `--no-edits` renders without any edits.";

    /// <summary>
    /// Look for the capture's saved edits and apply the chosen variant (<paramref name="variant"/>, or the database's active one) to
    /// <paramref name="st"/>. Returns a one-line note for the user, or null when nothing was applied (no database, or no active variant).
    /// A variant named explicitly that does not exist is an error.
    /// </summary>
    public static string? Apply(ExportState st, string lriPath, string? variant, Action<string>? log) => Apply(st, new ImageContext(lriPath), variant, log);

    /// <summary>The same for a capture whose context is already open (its content hash is computed once).</summary>
    public static string? Apply(ExportState st, ImageContext ctx, string? variant, Action<string>? log)
    {
        if (!ctx.HasEdits)
        {
            if (variant != null) throw new ExportRequestException($"--edits {variant}: this capture has no saved depth edits");
            return null;
        }
        using var store = DepthEditStore.Open(ctx.EditsDatabasePath, ctx.ContentHash, readOnly: true);
        string? name = variant ?? store.ActiveVariant;
        if (name == null) return null;
        var v = store.Find(name) ?? throw new ExportRequestException($"--edits {name}: no such variant (this capture has: {string.Join(", ", store.Variants().Select(x => x.Name))})");
        // The depth computed from the capture is only compared with the pinned one when it is already at hand (computed, set, or from the
        // analysis cache): running the whole registration chain for a depth the edits then replace would be wasted work.
        if (!st.FullDepthResolved && !st.HasGeometry && st.BuildLevel != 0) throw new ExportRequestException("saved depth edits need the capture's depth (a level-0 build)");
        bool? sameDepth = null;
        if (st.FullDepthResolved || st.HasGeometry)
        {
            if (st.FullDepth is not { } fd) throw new ExportRequestException("saved depth edits need the capture's depth (a level-0 build)");
            sameDepth = fd.W == v.DepthW && fd.H == v.DepthH && DepthEditStore.DepthFingerprint(fd.Depth, fd.W, fd.H).AsSpan().SequenceEqual(v.DepthSha);
        }
        // the depth the edits were made on replaces the freshly computed one for everything that reads the depth
        st.FullDepth = store.ReadPinnedDepth(v);
        var session = new DepthEditSession(st);
        var p = DepthEditPersistence.Open(store, session.Editor, v.Name);
        p.Detach();
        int tiles = store.ReadTileChecksums(v.Id).Count;
        st.EditedDepth = new AppliedDepthEdits(v.Name, v.Kind, session.Cache, session.Editor.Layer, tiles);
        string note = tiles == 0 && v.Kind == DepthEditStore.KindLumenImport
            ? $"saved depth: variant '{v.Name}' (no depth edits)"
            : v.Kind == DepthEditStore.KindRefined
            ? $"saved depth: variant '{v.Name}' (refined depth, {tiles} edited tile{(tiles == 1 ? "" : "s")})"
            : $"saved depth edits: variant '{v.Name}' ({tiles} edited tile{(tiles == 1 ? "" : "s")})"
            + (v.Kind == DepthEditStore.KindLumenImport ? ", imported from Lumen — it uses Lumen's stored depth map" : "")
            + (sameDepth == false && v.Kind != DepthEditStore.KindLumenImport ? "; the depth saved with the edits differs from the depth computed now and is the one used" : "");
        log?.Invoke(note);
        return note;
    }

    /// <summary>
    /// The render settings saved with the capture's variant (the named one, or the active one) — an imported Lumen session's settings, and
    /// for a Lumen import the session's camera geometry (its refined calibration + the pinned depth map), so that the export renders the
    /// capture as Lumen does after reopening the session. Null when there is no database, no active variant, or the variant has no settings.
    /// Opens the database read-only; nothing is written.
    /// </summary>
    public static SavedRenderState? LoadSavedState(ImageContext ctx, string? variant)
    {
        if (!ctx.HasEdits)
        {
            if (variant != null) throw new ExportRequestException($"--edits {variant}: this capture has no saved depth edits");
            return null;
        }
        using var store = DepthEditStore.Open(ctx.EditsDatabasePath, ctx.ContentHash, readOnly: true);
        // the capture's own Lux recipe (tone, crop / rotation, …) over the variant's saved settings; without a recipe exactly the variant's
        return CaptureRecipe.Merge(LoadVariantState(store, variant), store.ReadLuxRecipe());
    }

    /// <summary>What a capture renders with apart from its Lux recipe — the active variant's saved settings — and the recipe itself (the editor
    /// needs both: a recipe part that equals the variant's is not stored). (null, null) without an edit database.</summary>
    public static (SavedRenderState? Variant, CaptureRecipe? Recipe) LoadParts(ImageContext ctx)
    {
        if (!ctx.HasEdits) return (null, null);
        using var store = DepthEditStore.Open(ctx.EditsDatabasePath, ctx.ContentHash, readOnly: true);
        return (LoadVariantState(store, null), store.ReadLuxRecipe());
    }

    /// <summary>The render settings saved with a variant (the named one, or the active one): an imported Lumen session's, with its session
    /// geometry. Null when there is no such variant or it carries no settings.</summary>
    static SavedRenderState? LoadVariantState(DepthEditStore store, string? variant)
    {
        string? name = variant ?? store.ActiveVariant;
        if (name == null) return null;
        var v = store.Find(name) ?? throw new ExportRequestException($"--edits {name}: no such variant (this capture has: {string.Join(", ", store.Variants().Select(x => x.Name))})");
        if (store.ReadRenderSettings(v.Id) is not { } rs) return null;
        Func<Lux.Engine.Lri.LriFile, CaptureGeometry>? geometry = null;
        if (v.Kind == DepthEditStore.KindLumenImport && store.ReadBlob(v.Id, DepthEditPersistence.LumenStereoMessageBlob) is { } msg)
        {
            var depth = store.ReadPinnedDepth(v);
            var cal = LumenStereoState.Calibration(Ltpb.Stereo.Parser.ParseFrom(msg));
            geometry = lri => CaptureGeometry.FromSession(lri, cal, depth);
        }
        return new SavedRenderState
        {
            Variant = v.Name, Source = rs.Source, Settings = rs.HasSettings ? rs.Values : null, Transform = rs.ToExportTransform(), Rating = rs.Rating, ToneV2 = rs.ToneV2,
            SessionGeometry = geometry,
        };
    }

    /// <summary>Open a depth-editing session on a built capture, bound to its edit database: the active (or named) variant restored
    /// when there is one — on that variant's pinned depth — else a new native variant that is created at the first edit.</summary>
    public static (DepthEditSession Session, DepthEditPersistence Edits, DepthEditStore Store) OpenSession(ExportState st, string lriPath, string? variant = null, bool startClean = false)
    {
        var ctx = new ImageContext(lriPath);
        var store = DepthEditStore.OpenOrCreate(ctx);
        try
        {
            string? name = startClean ? null : variant ?? store.ActiveVariant;
            if (name != null)
            {
                var v = store.Find(name) ?? throw new DepthEditStoreException($"there is no variant '{name}'");
                st.FullDepth = store.ReadPinnedDepth(v);
                var s = new DepthEditSession(st);
                return (s, DepthEditPersistence.Open(store, s.Editor, name), store);
            }
            var fresh = new DepthEditSession(st);
            return (fresh, startClean ? DepthEditPersistence.StartClean(store, fresh.Editor) : DepthEditPersistence.New(store, fresh.Editor), store);
        }
        catch { store.Dispose(); throw; }
    }
}
