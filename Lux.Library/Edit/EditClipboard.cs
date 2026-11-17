using System.Globalization;
using System.Text.Json.Nodes;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;
using Lux.Library.Core;
using Lux.Store;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Edit;

/// <summary>
/// Copy / paste of edits and presets (<see cref="IEditClipboard"/>). A batch only writes the photos' recipes (no render) and refreshes their
/// mirrors, so their thumbnails are made again in the background; a photo open in the editor takes the change into its session instead (its
/// own save writes the same recipe). The recipe each photo had before a batch is kept in memory so the batch can be undone.
/// </summary>
internal sealed class EditClipboard : IEditClipboard
{
    readonly LibraryCore _lib;
    readonly object _lock = new();
    EditValues? _clip; string? _clipSource;
    readonly List<(long Id, List<(string CaptureId, CaptureRecipe? Before)> Photos)> _batches = new();
    long _nextBatch = 1;

    public event EventHandler? PresetsChanged;

    public EditClipboard(LibraryCore lib) { _lib = lib; }

    public EditValues? Clipboard { get { lock (_lock) return _clip; } }
    public string? ClipboardSource { get { lock (_lock) return _clipSource; } }

    public void SetClipboard(EditValues values, string? sourceCaptureId = null) { lock (_lock) { _clip = values; _clipSource = sourceCaptureId; } }

    /// <summary>A photo's edit values as saved (its open session's current state when it is open — saved first).</summary>
    internal EditValues ValuesOf(string captureId)
    {
        if (_lib.OpenSession(captureId) is { } open)
        {
            open.EditImpl.FlushSave();
            return open.EditImpl.Current;
        }
        var (state, _, _) = Load(captureId);
        return state;
    }

    /// <summary>A photo's saved state (variant settings + recipe) as edit values, with its recipe and transform base.</summary>
    (EditValues Values, CaptureRecipe? Recipe, SavedRenderState? Variant) Load(string captureId)
    {
        string path = _lib.PresentPath(captureId);
        var ctx = ImageContext.ForKnownCapture(path, captureId, _lib.Hashes, LocalContextStore.Current);
        var (variant, recipe) = SavedDepthEdits.LoadParts(ctx);
        var merged = CaptureRecipe.Merge(variant, recipe);
        var t = merged?.Transform is { } et ? LumenTransform.FromExport(et) : IdentityFor(captureId);
        return (new EditValues
        {
            Groups = EditGroups.All, Adjustments = merged?.Adjustments ?? Adjustments.Default, Transform = PhotoEditImpl.Describe(t),
            FNumber = merged?.FNumber, FocusMm = merged?.FocusDepth, DepthRendering = merged?.DepthEffectRendering ?? default,
        }, recipe, variant);
    }

    /// <summary>The transform an export applies to a photo shown the way the camera was held whose geometry is not edited (exports
    /// follow the automatic upright view): exactly what "Rotate as the camera was held" saves — Lumen's own rotate right /
    /// left on the capture's base transform (PhotoEditImpl.RotateAsHeld) — or null when nothing is added (held upright, geometry edited, or a
    /// base that is already turned). <paramref name="degrees"/> = the turn applied.</summary>
    internal ExportTransform? HeldTurnTransform(string captureId, out int degrees)
    {
        degrees = 0;
        var item = _lib.Get(captureId);
        int held = item is null ? 0 : PhotoEditImpl.HeldDegrees(item.HeldOrientation);
        if (held == 0) return null;
        var (_, recipe, variant) = Load(captureId);
        var cur = StateOf(captureId, variant, recipe);
        if (cur.Decided || LibraryCore.OrientationOf(cur.Geo.T.M) != PhotoOrientation.Normal) return null;
        var s = cur.Clone();
        PhotoEditImpl.Turn(s, held);
        degrees = held;
        return s.Geo.T.ToExport();
    }

    LumenTransform IdentityFor(string captureId)
    {
        var item = _lib.Get(captureId);
        var lv0 = item?.LevelSizes.FirstOrDefault() ?? new PixelSize(4, 3);
        var id0 = ExportTransform.Identity((Math.Max(1, lv0.Width), Math.Max(1, lv0.Height)));
        return LumenTransform.Identity(id0.AspectW, id0.AspectH);
    }

    public Task<EditValues> CopyAsync(string captureId, EditGroups groups, CancellationToken cancel = default) => Task.Run(() =>
    {
        var v = ValuesOf(captureId) with { Groups = groups };
        SetClipboard(v, captureId);
        return v;
    }, cancel);

    public Task<EditBatchResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel = default)
        => PasteAsync(captureIds, groups, null, cancel);

    public Task<EditBatchResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, IReadOnlySet<AdjustmentKey>? sliders, CancellationToken cancel = default)
    {
        var clip0 = Clipboard ?? throw new LibraryException("Nothing has been copied yet.");
        var clip = sliders is null ? clip0 : clip0 with { Sliders = sliders };
        return Task.Run(() => Batch(captureIds, (s, held, _) => PhotoEditImpl.ApplyTo(s, clip, groups, held), groups & clip.Groups, clip, cancel), cancel);
    }

    public Task<EditBatchResult> ResetAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel = default) => Task.Run(() =>
        Batch(captureIds, (s, _, reset) => (reset(s, groups), true), groups, null, cancel), cancel);

    /// <summary>One batch: per photo, its state (as the editor models it) → the change → the recipe written. The photo's previous recipe is kept.</summary>
    EditBatchResult Batch(IReadOnlyCollection<string> ids, Func<PhotoEditImpl.State, int, Func<PhotoEditImpl.State, EditGroups, PhotoEditImpl.State>, (PhotoEditImpl.State, bool)> change,
                          EditGroups groups, EditValues? values, CancellationToken cancel)
    {
        int written = 0, cropSkipped = 0;
        var failed = new List<(string, string)>();
        var before = new List<(string, CaptureRecipe?)>();
        foreach (var id in ids.Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (_lib.OpenSession(id) is { } open)
                {
                    // the open editor takes it (one undo step there); its save writes the recipe
                    open.EditImpl.FlushSave();
                    var (prevV, prevR, _) = Load(id);
                    _ = prevV;
                    bool ok = true;
                    if (values is not null) { var (_, cropOk) = PhotoEditImpl.ApplyTo(open.EditImpl.CurrentState, values, groups, open.EditImpl.DisplayTurnNow); ok = cropOk; open.EditImpl.TakeExternal(values, groups); }
                    else open.EditImpl.ResetGroups(groups);
                    open.EditImpl.FlushSaveForced();
                    before.Add((id, prevR));
                    if (!ok) cropSkipped++;
                    written++;
                    continue;
                }
                var (_, recipe, variant) = Load(id);
                var bstate = StateOf(id, variant, null);
                var cur = StateOf(id, variant, recipe);
                var item = _lib.Get(id);
                int held = _lib.SettingsImpl.ShowPhotosAsHeld && !cur.Decided && item is not null ? PhotoEditImpl.HeldDegrees(item.HeldOrientation) : 0;
                var (next, cropOk2) = change(cur, held, (s, g) => ResetOf(s, g, bstate));
                if (!cropOk2) cropSkipped++;
                var r = RecipeOf(next, bstate);
                RecipeIo.Write(_lib, id, _lib.PresentPath(id), r);
                before.Add((id, recipe));
                written++;
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                failed.Add((id, e is LibraryException ? e.Message : LibraryCore.Plain(e)));
            }
        }
        long bid;
        lock (_lock)
        {
            bid = _nextBatch++;
            _batches.Insert(0, (bid, before));
            if (_batches.Count > 10) _batches.RemoveAt(_batches.Count - 1);
        }
        return new EditBatchResult(written, cropSkipped, failed, bid);
    }

    /// <summary>The editor's state model of a photo without opening it: the variant base, the recipe over it.</summary>
    PhotoEditImpl.State StateOf(string id, SavedRenderState? variant, CaptureRecipe? recipe)
    {
        var t = variant?.Transform is { } vt ? LumenTransform.FromExport(vt) : IdentityFor(id);
        var s = new PhotoEditImpl.State { Adj = variant?.Adjustments ?? Adjustments.Default, Geo = new LumenGeometryEdits(t), FNumber = variant?.FNumber, Focus = variant?.FocusDepth,
                                          Decided = CaptureRecipe.IsGeometryDecided(recipe, variant?.Transform) };
        if (recipe is not null)
        {
            if (recipe.Adjustments is { } a) s.Adj = a;
            if (recipe.Transform is { } rt) s.Geo = new LumenGeometryEdits(LumenTransform.FromExport(rt));
            if (recipe.FNumber is float f) s.FNumber = f;
            if (recipe.FocusDepth is float fd) s.Focus = fd;
            s.Rendering = recipe.DepthEffectRendering;
        }
        return s;
    }

    static PhotoEditImpl.State ResetOf(PhotoEditImpl.State s0, EditGroups g, PhotoEditImpl.State bstate)
    {
        var s = s0.Clone();
        var d = Adjustments.Default; var a = s.Adj;
        if (g.HasFlag(EditGroups.Tone)) a = a with { Exposure = d.Exposure, Contrast = d.Contrast, Shadows = d.Shadows, Highlights = d.Highlights, Clarity = d.Clarity, HighlightRecovery = d.HighlightRecovery };
        if (g.HasFlag(EditGroups.Colour)) a = a with { Temperature = null, Tint = null, Vibrance = d.Vibrance, Saturation = d.Saturation, GamutCompress = d.GamutCompress, WhiteBalancePushed = false };
        if (g.HasFlag(EditGroups.Sharpening)) a = a with { Sharpening = d.Sharpening };
        s.Adj = a;
        if ((g & EditGroups.Geometry) == EditGroups.Geometry) { s.Geo.ResetGeometry(); s.Decided = false; }
        else if (g.HasFlag(EditGroups.Crop)) { LumenGeometryEdits.SetCropFromRect(s.Geo.T, new QRect(0, 0, 1, 1)); s.Geo.InitTransform(s.Geo.T); s.Geo.FitFrame(); }
        else if (g.HasFlag(EditGroups.RotationAndMirror))
        {
            var frame = LumenGeometryEdits.RectFromCrop(s.Geo.T);
            s.Geo.T.Reset(0); LumenGeometryEdits.SetCropFromRect(s.Geo.T, frame); s.Geo.InitTransform(s.Geo.T);
        }
        if (g.HasFlag(EditGroups.DepthEffect)) { s.FNumber = bstate.FNumber; s.Focus = bstate.Focus; s.Rendering = bstate.Rendering; }
        return s;
    }

    static CaptureRecipe? RecipeOf(PhotoEditImpl.State st, PhotoEditImpl.State bstate)
    {
        var r = new CaptureRecipe
        {
            Adjustments = st.Adj == bstate.Adj ? null : st.Adj,
            Transform = st.Geo.T.SameAs(bstate.Geo.T) ? null : st.Geo.T.ToExport(),
            FNumber = st.FNumber == bstate.FNumber ? null : st.FNumber,
            FocusDepth = st.Focus == bstate.Focus ? null : st.Focus,
            DepthEffectRendering = st.Rendering,
            GeometryDecided = st.Decided == bstate.Decided ? null : st.Decided,
        };
        return r.IsEmpty ? null : r;
    }

    public IReadOnlyList<long> UndoableBatches { get { lock (_lock) return _batches.Select(b => b.Id).ToList(); } }

    public Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel = default) => Task.Run(() =>
    {
        List<(string CaptureId, CaptureRecipe? Before)>? photos;
        lock (_lock)
        {
            int i = _batches.FindIndex(b => b.Id == batchId);
            if (i < 0) return false;
            photos = _batches[i].Photos; _batches.RemoveAt(i);
        }
        foreach (var (id, rec) in photos)
        {
            cancel.ThrowIfCancellationRequested();
            if (_lib.OpenSession(id) is { } open)
            {
                // the editor: back to the saved recipe of before the batch, as a change of its own
                var (_, _, variant) = Load(id);
                open.EditImpl.TakeState(StateOf(id, variant, rec), "undo paste");
                open.EditImpl.FlushSaveForced();
                continue;
            }
            RecipeIo.Write(_lib, id, _lib.PresentPath(id), rec);
        }
        return true;
    }, cancel);

    // ------------------------------------------------------------------------------------------------ presets (library database, table `preset`)

    public IReadOnlyList<EditPreset> GetPresets() => _lib.Db.Read(c => c.Rows(null, "SELECT id, name, groups, json, created_utc FROM preset ORDER BY name COLLATE NOCASE",
        r => new EditPreset(r.GetInt64(0), r.GetString(1), (EditGroups)r.GetInt32(2), EditValuesCodec.FromJson(r.GetString(3)), new DateTime(r.GetInt64(4), DateTimeKind.Utc))));

    EditPreset Preset(long id) => GetPresets().FirstOrDefault(p => p.Id == id) ?? throw new LibraryException("There is no such preset.");

    static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new LibraryException("A preset needs a name.");
        if (n.Length > 120) throw new LibraryException("A preset name can be at most 120 characters long.");
        return n;
    }

    public Task<EditPreset> CreatePresetAsync(string name, EditValues values, EditGroups groups, CancellationToken cancel = default) => Task.Run(() =>
    {
        string n = CleanName(name);
        long now = DateTime.UtcNow.Ticks;
        long id;
        try
        {
            id = _lib.Db.Write((c, tx) => c.Long(tx, "INSERT INTO preset(name, groups, json, created_utc, changed_utc) VALUES($n, $g, $j, $u, $u) RETURNING id",
                ("$n", n), ("$g", (long)(groups & EditGroups.All)), ("$j", EditValuesCodec.ToJson(values with { Groups = groups })), ("$u", now)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new LibraryException($"A preset named “{n}” exists already."); }
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        return Preset(id);
    }, cancel);

    public Task<EditPreset> RenamePresetAsync(long presetId, string newName, CancellationToken cancel = default) => Task.Run(() =>
    {
        string n = CleanName(newName);
        try
        {
            int k = _lib.Db.Write((c, tx) => c.Exec(tx, "UPDATE preset SET name=$n, changed_utc=$u WHERE id=$i", ("$n", n), ("$u", DateTime.UtcNow.Ticks), ("$i", presetId)));
            if (k == 0) throw new LibraryException("There is no such preset.");
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new LibraryException($"A preset named “{n}” exists already."); }
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        return Preset(presetId);
    }, cancel);

    public Task<EditPreset> UpdatePresetAsync(long presetId, EditValues values, EditGroups groups, CancellationToken cancel = default) => Task.Run(() =>
    {
        int k = _lib.Db.Write((c, tx) => c.Exec(tx, "UPDATE preset SET groups=$g, json=$j, changed_utc=$u WHERE id=$i",
            ("$g", (long)(groups & EditGroups.All)), ("$j", EditValuesCodec.ToJson(values with { Groups = groups })), ("$u", DateTime.UtcNow.Ticks), ("$i", presetId)));
        if (k == 0) throw new LibraryException("There is no such preset.");
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        return Preset(presetId);
    }, cancel);

    public Task DeletePresetAsync(long presetId, CancellationToken cancel = default) => Task.Run(() =>
    {
        _lib.Db.Write((c, tx) => c.Exec(tx, "DELETE FROM preset WHERE id=$i", ("$i", presetId)));
        PresetsChanged?.Invoke(this, EventArgs.Empty);
    }, cancel);

    public Task<EditBatchResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default) => Task.Run(() =>
    {
        var p = Preset(presetId);
        return Batch(captureIds, (s, held, _) => PhotoEditImpl.ApplyTo(s, p.Values, p.Groups, held), p.Groups, p.Values, cancel);
    }, cancel);
}

/// <summary>JSON of <see cref="EditValues"/> (presets): floats as 32-bit patterns, so a preset applies exactly the values it was made with.</summary>
internal static class EditValuesCodec
{
    static uint B(float v) => BitConverter.SingleToUInt32Bits(v);
    static float F(JsonNode? n) => BitConverter.UInt32BitsToSingle(n!.GetValue<uint>());

    public static string ToJson(EditValues v)
    {
        var a = v.Adjustments; var t = v.Transform;
        var o = new JsonObject
        {
            ["version"] = 1, ["groups"] = (int)v.Groups,
            ["adjustments"] = new JsonObject
            {
                ["exposure"] = B(a.Exposure), ["contrast"] = B(a.Contrast), ["temperature"] = a.Temperature is float tt ? JsonValue.Create(B(tt)) : null,
                ["tint"] = a.Tint is float tn ? JsonValue.Create(B(tn)) : null, ["vibrance"] = B(a.Vibrance), ["saturation"] = B(a.Saturation),
                ["sharpening"] = B(a.Sharpening), ["shadows"] = B(a.Shadows), ["highlights"] = B(a.Highlights), ["clarity"] = B(a.Clarity),
            },
            ["transform"] = new JsonObject
            {
                ["aspect"] = new JsonArray(t.AspectWidth, t.AspectHeight), ["crop"] = new JsonArray(B(t.CropX0), B(t.CropY0), B(t.CropX1), B(t.CropY1)),
                ["matrix"] = new JsonArray(t.Matrix.Select(x => (JsonNode)JsonValue.Create(B(x))).ToArray()),
            },
            ["fnumber"] = v.FNumber is float f ? JsonValue.Create(B(f)) : null, ["focus"] = v.FocusMm is float fo ? JsonValue.Create(B(fo)) : null,
            ["sliders"] = v.Sliders is { } sl ? new JsonArray(sl.OrderBy(k => k).Select(k => (JsonNode)JsonValue.Create((int)k)).ToArray()) : null,
        };
        // The two Lux settings are written only when they are set. A preset without them has the same text as before, and a preset
        // from an earlier version reads as "not set".
        var ao = o["adjustments"]!.AsObject();
        if (a.HighlightRecovery != 0f) ao["highlight_recovery"] = B(a.HighlightRecovery);
        if (a.GamutCompress) ao["gamut_compress"] = 1;
        if (v.DepthRendering == Lux.Engine.Pipeline.Dof.DofRendering.Lux) o["dof_rendering"] = "lux";
        return o.ToJsonString();
    }

    public static EditValues FromJson(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var a = o["adjustments"]!.AsObject();
        var t = o["transform"]!.AsObject();
        var asp = t["aspect"]!.AsArray(); var c = t["crop"]!.AsArray(); var m = t["matrix"]!.AsArray();
        var lt = new LumenTransform { AspectW = asp[0]!.GetValue<int>(), AspectH = asp[1]!.GetValue<int>(), X0 = F(c[0]), Y0 = F(c[1]), X1 = F(c[2]), Y1 = F(c[3]) };
        for (int i = 0; i < 9; i++) lt.M[i] = F(m[i]);
        return new EditValues
        {
            Groups = (EditGroups)o["groups"]!.GetValue<int>(),
            Adjustments = new Adjustments
            {
                Exposure = F(a["exposure"]), Contrast = F(a["contrast"]), Temperature = a["temperature"] is { } tt ? F(tt) : null, Tint = a["tint"] is { } tn ? F(tn) : null,
                Vibrance = F(a["vibrance"]), Saturation = F(a["saturation"]), Sharpening = F(a["sharpening"]), Shadows = F(a["shadows"]),
                Highlights = F(a["highlights"]), Clarity = F(a["clarity"]),
                HighlightRecovery = a["highlight_recovery"] is { } hr ? F(hr) : 0f, GamutCompress = a["gamut_compress"] is { } gc && gc.GetValue<int>() != 0,
            },
            Transform = PhotoEditImpl.Describe(lt),
            FNumber = o["fnumber"] is { } fn ? F(fn) : null, FocusMm = o["focus"] is { } fo ? F(fo) : null,
            DepthRendering = o["dof_rendering"] is JsonValue dr && dr.TryGetValue<string>(out var drs) && drs == "lux" ? Lux.Engine.Pipeline.Dof.DofRendering.Lux : Lux.Engine.Pipeline.Dof.DofRendering.Lumen,
            Sliders = o["sliders"] is JsonArray sl ? sl.Select(x => (AdjustmentKey)x!.GetValue<int>()).ToHashSet() : null,
        };
    }
}
