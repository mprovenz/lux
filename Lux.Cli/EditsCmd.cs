using System.Globalization;
using Lux.Store;

namespace Lux.Cli;

/// <summary>`lux-light edits-info` and `lux-light import-lumen-edits`: the saved depth edits of a capture (its database in the
/// `LuxImageContext` folder next to it). See <see cref="SavedDepthEdits"/> for how `convert` uses them.</summary>
public static class EditsCmd
{
    static string Mb(long bytes) => (bytes / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " MB";

    /// <summary>`edits-info &lt;lri…&gt; [--set-active &lt;variant|none&gt;]`: what is saved for each capture, and whether it is sound.</summary>
    public static int Info(List<string> files, Options o)
    {
        int rc = 0;
        foreach (var path in files)
        {
            var ctx = new ImageContext(path);
            Console.WriteLine($"{Path.GetFileName(path)}");
            if (!ctx.HasEdits)
            {
                Console.WriteLine("  no saved depth edits");
                if (o.SetActive != null) { Console.Error.WriteLine("  error: --set-active needs a capture with saved depth edits"); rc = 2; }
                continue;
            }
            try
            {
                using var store = DepthEditStore.Open(ctx.EditsDatabasePath, ctx.ContentHash, readOnly: o.SetActive == null);
                if (o.SetActive is string want)
                {
                    store.ActiveVariant = want is "none" ? null : want;
                    Console.WriteLine(want is "none" ? "  no variant is active now: the capture renders without edits" : $"  active variant set to '{want}'");
                }
                string? active = store.ActiveVariant; var vs = store.Variants();
                Console.WriteLine($"  database   {store.Path} ({Mb(new FileInfo(store.Path).Length)})");
                Console.WriteLine($"  capture    sha-256 {store.CaptureHash}, {store.CaptureBytes} bytes");
                Console.WriteLine($"  variants   {vs.Count}; active: {active ?? "none (renders without edits)"}");
                foreach (var v in vs)
                {
                    var sz = store.Sizes(v);
                    var kinds = new Dictionary<string, int>();
                    try { foreach (var e in store.ReadLog(v.Id)) kinds[e.Kind] = kinds.GetValueOrDefault(e.Kind) + 1; } catch (DepthEditStoreException) { }
                    string what = v.Kind == DepthEditStore.KindLumenImport ? "imported from Lumen (uses Lumen's stored depth map and camera geometry)" : "made in Lux";
                    Console.WriteLine($"  {(v.Name == active ? "*" : " ")} {v.Name}: {what}, created {v.CreatedUtc}");
                    Console.WriteLine($"      operations   {sz.LogEntries}" + (kinds.Count > 0 ? " (" + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}")) + ")" : "") + $", undo position {v.Cursor}");
                    Console.WriteLine($"      edited tiles {sz.Tiles} of {v.Geometry.TileW}x{v.Geometry.TileH} px ({sz.LayeredTiles} with a foreground/background layer), {Mb(sz.TileBlobBytes)} stored for {Mb(sz.TileRawBytes)} of data");
                    Console.WriteLine($"      base depth   {v.DepthW}x{v.DepthH}, {Mb(sz.PinnedBlobBytes)} stored for {Mb(sz.PinnedRawBytes)}" + (sz.ImportBytes > 0 ? $"; original Lumen data kept: {Mb(sz.ImportBytes)}" : ""));
                    try
                    {
                        if (store.ReadRenderSettings(v.Id) is { } rs) Console.WriteLine($"      settings     {rs.Describe()}{(rs.Source == StoredSettings.SourceLumenSession ? " (from the Lumen session)" : "")}");
                    }
                    catch (DepthEditStoreException e) { Console.WriteLine($"      settings     damaged: {e.Message}"); }
                    Console.WriteLine($"      history      {v.History switch { HistoryState.Verified => "verified (replaying the operations gives the saved result)", HistoryState.Unreliable => "UNRELIABLE — the saved result is kept, but undo cannot go back before this session: " + v.HistoryNote, _ => "not checked yet" }}");
                }
                var problems = store.Validate();
                if (problems.Count == 0) Console.WriteLine("  validation OK: every stored block matches its checksum");
                else { rc = 1; Console.WriteLine($"  validation FAILED ({problems.Count}):"); foreach (var p in problems) Console.WriteLine($"    {p}"); }
            }
            catch (DepthEditStoreException e) { rc = 1; Console.WriteLine($"  error: {e.Message}"); }
        }
        return rc;
    }

    /// <summary>`import-lumen-edits &lt;lris file or folder…&gt;`: bring Lumen session files into Lux — the depth map Lumen stored, the depth
    /// edits when there are any, and the per-photo settings (depth effect, tone and colour, crop / rotation, rating).</summary>
    public static int ImportLumen(List<string> inputs)
    {
        var sessions = new List<string>();
        foreach (var a in inputs)
        {
            if (Directory.Exists(a)) sessions.AddRange(Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".lris", StringComparison.OrdinalIgnoreCase)));
            else if (File.Exists(a) && a.EndsWith(".lris", StringComparison.OrdinalIgnoreCase)) sessions.Add(a);
            else { Console.Error.WriteLine($"error: {a} is neither a .lris file nor a folder"); return 2; }
        }
        sessions.Sort(StringComparer.Ordinal);
        if (sessions.Count == 0) { Console.Error.WriteLine("error: no .lris files found."); return 1; }
        int imported = 0, noCapture = 0, failed = 0, already = 0, withEdits = 0, updated = 0;
        foreach (var s in sessions)
        {
            string name = Path.GetFileName(s);
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(s)) ?? ".", stem = Path.GetFileNameWithoutExtension(s);
                string? lri = Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == stem && f.EndsWith(".lri", StringComparison.OrdinalIgnoreCase));
                if (lri == null) { noCapture++; Console.WriteLine($"{name}: there is no {stem}.lri beside it — skipped"); continue; }
                var r = LumenEditImport.Import(lri, s);
                string what = r.Settings?.Describe() ?? "no settings";
                if (r.AlreadyImported) { already++; Console.WriteLine($"{name}: already imported (variant '{r.Variant}')"); continue; }
                if (r.Updated) { updated++; Console.WriteLine($"{name}: added the session's settings ({what}) to the depth edits imported earlier as variant '{r.Variant}'"); continue; }
                imported++; if (r.Tiles > 0) withEdits++;
                string edits = r.Tiles > 0 ? $"{r.Tiles} edited depth tile{(r.Tiles == 1 ? "" : "s")} ({r.LayeredTiles} with a foreground/background layer), " : "no depth edits, ";
                Console.WriteLine($"{name}: imported for {Path.GetFileName(lri)} as variant '{r.Variant}', now the active one: {edits}Lumen's depth map, settings {what}");
            }
            catch (Exception e) when (e is DepthEditStoreException or InvalidDataException or IOException or InvalidOperationException)
            { failed++; Console.WriteLine($"{name}: FAILED: {e.Message}"); }
        }
        Console.WriteLine($"\n{sessions.Count} session file(s): {imported} imported ({withEdits} with depth edits), {updated} updated, {already} already imported, {noCapture} without their capture, {failed} failed");
        if (imported + already + updated > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Note: " + SavedDepthEdits.LumenImportWarning);
            Console.WriteLine("`lux-light convert` now renders these captures the way Lumen does after reopening the session: Lumen's depth map and camera"
                            + " geometry, the depth edits, the depth effect (f-number and focus), the tone and colour settings and the crop / rotation."
                            + " Options given to `convert` override the saved settings; `--no-edits` ignores all of it. `lux-light edits-info <lri>` shows what is saved.");
        }
        return failed == 0 ? 0 : 1;
    }
}
