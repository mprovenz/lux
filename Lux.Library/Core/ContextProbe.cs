using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lux.Engine.Session;
using Lux.Store;
using Microsoft.Data.Sqlite;

namespace Lux.Library.Core;

/// <summary>
/// Reads what a capture's <c>LuxImageContext</c> holds — the edit database (<c>edits/&lt;hash&gt;.luxedits</c>: variants, history, saved
/// settings, rating) and the analysis cache (<c>cache/&lt;hash&gt;.luxcache</c>: level, size) — for the library's mirror. Read-only: it
/// never creates, migrates or discards anything (a stale cache simply reads as level none; the engine discards it when it next opens it).
/// </summary>
internal static class ContextProbe
{
    public static string ContextRoot(string dir) => Path.Combine(dir, ImageContext.FolderName);
    public static string EditsPath(string dir, string id) => Path.Combine(dir, ImageContext.FolderName, ImageContext.EditsFolderName, id + ImageContext.EditsExtension);
    public static string CachePath(string dir, string id) => Path.Combine(dir, ImageContext.FolderName, ImageContext.CacheFolderName, id + AnalysisCacheStore.Extension);
    /// <summary>The edit database / cache file of a capture inside a context root (<c>…/LuxImageContext</c> or <c>&lt;local store&gt;/&lt;hash&gt;</c>).</summary>
    public static string EditsPathIn(string root, string id) => Path.Combine(root, ImageContext.EditsFolderName, id + ImageContext.EditsExtension);
    public static string CachePathIn(string root, string id) => Path.Combine(root, ImageContext.CacheFolderName, id + AnalysisCacheStore.Extension);

    /// <summary>A change stamp of a file and its SQLite side files: "-" when absent.</summary>
    public static string Stamp(string path)
    {
        var sb = new StringBuilder();
        foreach (var p in new[] { path, path + "-wal" })
        {
            var fi = new FileInfo(p);
            if (fi.Exists) sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            else sb.Append("-;");
        }
        return sb.ToString();
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>What the edit database says.</summary>
    /// <param name="Rating">The capture's own rating: the app-set one (meta) when present, else the active variant's saved one (a Lumen
    /// session's); null = none of its own.</param>
    public sealed record EditsMirror(int? Rating, EditSummary Summary, string ThumbKey, string? Error, StoredSettings? Settings = null)
    {
        /// <summary>The geometry is a decision of its own (<see cref="CaptureRecipe.IsGeometryDecided"/>): no display-only held turn.</summary>
        public bool GeometryDecided { get; init; }
    }

    public static EditsMirror ReadEdits(string path, string id)
    {
        try
        {
            using var s = DepthEditStore.Open(path, id, readOnly: true);
            int? rating = s.CaptureRating;
            var variants = s.Variants();
            string? active = s.ActiveVariant;
            var v = active is null ? null : variants.FirstOrDefault(x => x.Name == active);
            // the capture's own Lux recipe (tone, crop / rotation, …), over the variant's settings
            CaptureRecipe? recipe = null; string? recipeErr = null;
            try { recipe = s.ReadLuxRecipe(); }
            catch (DepthEditStoreException e) { recipeErr = e.Message; }
            string recipeSha = recipe is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipe.ToJson())), 0, 8);
            if (v is null)
            {
                if (recipe is null || recipe.IsEmpty)
                    return new EditsMirror(rating, EditSummary.None with { VariantCount = variants.Count }, "clean", recipeErr);
                return new EditsMirror(rating, EditSummary.None with { VariantCount = variants.Count, HasSettings = recipe.HasRenderParts }, "clean|lux " + recipeSha, recipeErr,
                                       Effective(null, recipe)) { GeometryDecided = CaptureRecipe.IsGeometryDecided(recipe, null) };
            }
            StoredSettings? rs = null; string? err = recipeErr;
            try { rs = s.ReadRenderSettings(v.Id); }
            catch (DepthEditStoreException e) { err = e.Message; }
            // a Lumen session's rating counts only when it rated the photo (Lumen writes 0 for unrated); an app-set rating counts even at 0
            rating ??= rs?.Rating is uint r && r > 0 ? (int)Math.Min(5u, r) : null;
            int ops = (int)s.LogCount(v.Id);
            var src = v.Kind == DepthEditStore.KindLumenImport ? EditSource.Lumen : EditSource.Lux;
            var eff = Effective(rs, recipe);
            var summary = new EditSummary(v.Name, ops, src, variants.Count, eff is not null && (eff.HasSettings || eff.ToExportTransform() is not null), v.History == HistoryState.Unreliable);
            string settingsSha = rs is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rs.ToJson())), 0, 8);
            string key = string.Create(CultureInfo.InvariantCulture, $"{v.Name}|{v.Id}|{v.Cursor}|{ops}|{settingsSha}") + (recipe is null ? "" : "|lux " + recipeSha);
            return new EditsMirror(rating, summary, key, err, eff) { GeometryDecided = CaptureRecipe.IsGeometryDecided(recipe, rs?.ToExportTransform()) };
        }
        catch (Exception e) when (e is DepthEditStoreException or SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new EditsMirror(null, EditSummary.None, "clean", "The saved edits of this photo cannot be read: " + e.Message);
        }
    }

    /// <summary>The settings a capture renders with, as one <see cref="StoredSettings"/> for the mirror: the variant's (or none) with the Lux recipe's
    /// parts over them (the same rule as <see cref="CaptureRecipe.Merge"/>).</summary>
    internal static StoredSettings? Effective(StoredSettings? rs, CaptureRecipe? recipe)
    {
        if (recipe is null || !recipe.HasRenderParts) return rs;
        var values = rs is null ? (float[])Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings.Defaults.Clone() : (float[])rs.Values.Clone();
        var present = rs is null ? new bool[Lux.Engine.Pipeline.Dof.Edit.LumenRendererSettings.Count] : (bool[])rs.Present.Clone();
        void Set(int i, float v) { values[i] = v; present[i] = true; }
        if (recipe.Adjustments is { } a)
        {
            Set(2, a.Exposure); Set(5, a.Shadows); Set(6, a.Highlights); Set(7, a.Contrast); Set(8, a.Saturation); Set(9, a.Vibrance); Set(10, a.Clarity); Set(13, a.Sharpening);
            if (a.Temperature is float t) Set(3, t); else present[3] = false;
            if (a.Tint is float u) Set(4, u); else present[4] = false;
        }
        if (recipe.FNumber is float f) Set(0, f);
        if (recipe.FocusDepth is float fd) Set(1, fd);
        var tr = recipe.Transform;
        return new StoredSettings
        {
            Source = CaptureRecipe.SourceLux, SourceSha = rs?.SourceSha, Values = values, Present = present,
            Aspect = tr is not null ? (tr.AspectW, tr.AspectH) : rs?.Aspect, Crop = tr is not null ? (tr.CropX0, tr.CropY0, tr.CropX1, tr.CropY1) : rs?.Crop,
            Matrix = tr is not null ? (float[])tr.Matrix.Clone() : rs?.Matrix, Rating = rs?.Rating, Uuid = rs?.Uuid, Version = rs?.Version, ToneV2 = rs?.ToneV2 ?? false,
        };
    }

    /// <summary>The cache level and disk size of a capture's analysis cache; level none when absent, stale (another engine version) or
    /// unreadable.</summary>
    public static (CacheLevel Level, long Bytes) ReadCache(string path, string id)
    {
        if (!File.Exists(path)) return (CacheLevel.None, 0);
        long bytes = new[] { path, path + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            c.Open();
            string? Meta(string k) => c.Scalar(null, "SELECT value FROM meta WHERE key=$k", ("$k", k))?.ToString();
            if (Meta("schema_version") != AnalysisCacheStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                || !string.Equals(Meta("capture_sha256"), id, StringComparison.OrdinalIgnoreCase)
                || Meta("engine_cache_version") != AnalysisCache.EngineCacheVersion.ToString(CultureInfo.InvariantCulture))
                return (CacheLevel.None, bytes);
            var complete = c.Rows(null, "SELECT name FROM tier WHERE complete=1 AND descriptor IS NOT NULL", r => r.GetString(0)).ToHashSet();
            var level = CacheLevel.None;
            foreach (var l in new[] { CacheLevel.Geometry, CacheLevel.Linear13, CacheLevel.Full })
            {
                if (!complete.Contains(AnalysisCache.TierName(l))) break;
                level = l;
            }
            return (level, bytes);
        }
        catch (SqliteException) { return (CacheLevel.None, bytes); }
    }
}
