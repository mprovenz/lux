using Lux.Engine.Lri;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;

namespace Lux.Store;

/// <summary>
/// Import of a Lumen session file (<c>.lris</c>) into the capture's edit database, as a variant of kind
/// <see cref="DepthEditStore.KindLumenImport"/> — everything Lumen restores when Lumen reopens the session:
/// <list type="bullet">
/// <item>its pinned base depth is the depth map Lumen saved in the session (the Stereo record, quantized by Lumen's 14-bit depth codec);</item>
/// <item>its snapshot is what Lumen has after reloading the depth edits, if the session has any: the stored tiles as the tiles' only version,
/// written into the depth cache and the edit layer (the DepthEditor record is kept verbatim and the log starts with a <c>lumen-import</c>
/// entry);</item>
/// <item>its render settings (<see cref="StoredSettings"/>) are the session's RendererState, Transform and MetaData records as Lumen loads
/// them — f-number, focus depth, the tone and color settings, sharpening, crop / rotation, rating;</item>
/// <item>the Stereo record's message (the refined calibration of every module) is kept, so that a render rebuilds the session's camera
/// geometry (<see cref="Lux.Engine.Session.CaptureGeometry.FromSession"/>) — nothing depends on the <c>.lris</c> file afterwards.</item>
/// </list>
/// Nothing is rendered or computed from the capture: only its header is read.
/// </summary>
public static class LumenEditImport
{
    public sealed record Result(string Variant, int Tiles, int LayeredTiles, bool AlreadyImported, string DatabasePath, StoredSettings? Settings, bool Updated = false);

    /// <summary>The export-window level tables of a capture with Lumen's default crop — the frame the depth editor works in.</summary>
    public static ExportLevels Levels(LriFile lri)
    {
        lri.Frame(lri.Modules[lri.ReferenceModule], out int w, out int h);
        var sensor = (w, h);
        return ExportWindow.Compute(ExportWindow.Canvas(lri, sensor), sensor, ExportWindow.CropRect(lri, sensor), 5);
    }

    /// <summary>Whether a session file holds depth edits at all (it has a DepthEditor record).</summary>
    public static bool HasDepthEdits(string lrisPath) => LumenSessionReader.Read(lrisPath, decode: false).HasDepthEdits;

    /// <summary>
    /// Import <paramref name="lrisPath"/> for the capture <paramref name="lriPath"/>. The imported variant becomes the active one. A session
    /// already imported (same file) is not imported twice; a variant imported earlier from the same depth edits without settings gets the
    /// settings added. Throws <see cref="DepthEditStoreException"/> / <see cref="InvalidDataException"/> for a session Lumen could not load
    /// for this capture (another photo's session, a transform of another aspect ratio, no depth map in the current format, …).
    /// </summary>
    /// <param name="toneV2">Lumen's newer tone style for this capture (the GUI renderer's v2 tone gate, which the session does not record): kept in
    /// the variant's settings (<see cref="StoredSettings.ToneV2"/>) so exports apply it. Importing the same session again with another choice
    /// updates it.</param>
    public static Result Import(string lriPath, string lrisPath, IContentHashCache? hashes = null, bool toneV2 = false)
    {
        var bytes = File.ReadAllBytes(lrisPath);
        var session = LumenSessionReader.Read(bytes);
        var settings = session.Settings;
        if (session.Stereo is not { DepthType: LumenDepthType.Dpc } stereo)
            throw new DepthEditStoreException("the session holds no depth map in Lumen's current format; it cannot be imported");
        var lri = LriFile.Load(lriPath);
        // the renderer refuses a session of another photo: "Metadata UUID in state file doens't match image UUID!"
        var h = lri.Header;
        if (settings.MetaData.UuidLow is not ulong lo || settings.MetaData.UuidHigh is not ulong hi)
            throw new DepthEditStoreException("the session has no image UUID; Lumen itself cannot load it");
        if (lo != h.ImageUniqueIdLow || hi != h.ImageUniqueIdHigh)
            throw new DepthEditStoreException($"the session belongs to another photo (image UUID {lo:x16}{hi:x16}, this capture {h.ImageUniqueIdLow:x16}{h.ImageUniqueIdHigh:x16})");
        var lv = Levels(lri);
        settings.Transform?.ToExportTransform(lv.ExportDims[0]);   // throws for another aspect ratio, as Lumen's reader does
        var rs = StoredSettings.FromSession(settings, PlaneCodec.Sha(bytes));
        if (toneV2) rs = new StoredSettings
        {
            Source = rs.Source, SourceSha = rs.SourceSha, Values = rs.Values, Present = rs.Present, Aspect = rs.Aspect, Crop = rs.Crop, Matrix = rs.Matrix,
            Rating = rs.Rating, Uuid = rs.Uuid, Version = rs.Version, ToneV2 = true,
        };
        var stereoMessage = stereo.Record.Proto.ToArray();

        var ctx = new ImageContext(lriPath, hashes);
        using var store = DepthEditStore.OpenOrCreate(ctx);
        var edits = session.DepthEditor;
        var recSha = edits is null ? null : PlaneCodec.Sha(edits.Record.Bytes.Span);
        int tiles = edits?.Tiles.Count ?? 0, layered = edits?.Tiles.Count(t => t.Mask != null) ?? 0;
        foreach (var v in store.Variants())
        {
            if (v.Kind != DepthEditStore.KindLumenImport) continue;
            var have = store.ReadRenderSettings(v.Id);
            if (have?.SourceSha is { } ss && ss.AsSpan().SequenceEqual(rs.SourceSha!))
            {
                if (have.ToneV2 == toneV2) return new Result(v.Name, tiles, layered, true, store.Path, have);
                store.WriteRenderSettings(v.Id, rs);   // the same session, the tone style chosen differently
                return new Result(v.Name, tiles, layered, false, store.Path, rs, Updated: true);
            }
            // a variant imported by an earlier Lux from the same depth edits, without settings: add them
            if (have is null && recSha is not null && store.BlobSha(v.Id, DepthEditPersistence.LumenRecordBlob) is { } b && b.AsSpan().SequenceEqual(recSha))
            {
                store.InTransaction(() =>
                {
                    store.WriteRenderSettings(v.Id, rs);
                    if (store.BlobSha(v.Id, DepthEditPersistence.LumenStereoMessageBlob) is null) store.WriteBlob(v.Id, DepthEditPersistence.LumenStereoMessageBlob, stereoMessage);
                });
                return new Result(v.Name, tiles, layered, false, store.Path, rs, Updated: true);
            }
        }

        var depth = stereo.DecodeDepth(out int dw, out int dh);
        int l = DepthEditor.EditLevel(lv);
        if (edits is not null && (edits.TileSize != (DepthEditor.TileW, DepthEditor.TileH) || edits.DepthSize != lv.ExportDims[l]))
            throw new DepthEditStoreException($"the session's depth edits are {edits.DepthSize.W}x{edits.DepthSize.H} in tiles of {edits.TileSize.W}x{edits.TileSize.H}; this capture is edited at {lv.ExportDims[l].W}x{lv.ExportDims[l].H}");
        var cache = DepthImageCache.FromFullDepth(depth, dw, dh, lv);
        var editor = new DepthEditor(cache, new DepthImage(depth, dw, dh, dw, new RectI(0, 0, dw, dh)), lv.ExportDims[l], lv.Origins[l]) { Level = l };
        string note = $"imported from {Path.GetFileName(lrisPath)} (session sha-256 {Convert.ToHexString(rs.SourceSha!).ToLowerInvariant()}, "
                    + $"depth map sha-256 {Convert.ToHexString(PlaneCodec.Sha(stereo.Record.Bytes.Span)).ToLowerInvariant()})";
        DepthEditPersistence p;
        if (edits is not null)
        {
            p = DepthEditPersistence.New(store, editor, DepthEditStore.KindLumenImport, DepthEditStore.KindLumenImport);
            if (!p.ImportLumen(edits.Record.Bytes.Span, note)) throw new DepthEditStoreException("the session's depth edits could not be applied");
        }
        else p = DepthEditPersistence.CreateNow(store, editor, DepthEditStore.KindLumenImport, DepthEditStore.KindLumenImport);
        var vi = p.Variant!;
        store.InTransaction(() =>
        {
            store.WriteRenderSettings(vi.Id, rs);
            store.WriteBlob(vi.Id, DepthEditPersistence.LumenStereoMessageBlob, stereoMessage);
        });
        p.Detach();
        return new Result(vi.Name, tiles, layered, false, store.Path, rs);
    }
}
