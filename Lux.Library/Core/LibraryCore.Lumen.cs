using System.Globalization;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;

namespace Lux.Library.Core;

internal sealed partial class LibraryCore
{
    /// <summary>A session file's change stamp, with the classifier's version (a new rule classifies every session again).</summary>
    internal static string LrisStamp(string lris) { var fi = new FileInfo(lris); return fi.Exists ? $"c2|{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "-"; }

    /// <summary>What a Lumen session did to its capture: depth edits (a DepthEditor record); else settings that differ from a fresh renderer of
    /// this capture — exactly as `dof-test lris-scan --exact` (`LumenSessionSettings.HasNonDefault` with the capture's all-in-focus f-number and
    /// loaded white balance: a crop / rotation, a rating, the f-number, focus, any tone or colour value); else opened but unchanged.</summary>
    internal static LumenSessionState ClassifyLumen(LriFile lri, string lrisPath)
    {
        try
        {
            var s = LumenSessionReader.Read(lrisPath, decode: false);
            if (s.HasDepthEdits) return LumenSessionState.DepthEdited;
            var wb = Lux.Engine.Pipeline.Color.WhiteBalance.CaptureWb.From(lri, Lux.Engine.Pipeline.Color.LumenProfile.Compute(lri));
            return s.Settings.HasNonDefault(ExportWindow.AllInFocusFNumber(lri), wb.Cct, wb.Tint) ? LumenSessionState.SettingsChanged : LumenSessionState.OpenedUnchanged;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Google.Protobuf.InvalidProtocolBufferException or KeyNotFoundException)
        {
            return LumenSessionState.OpenedUnchanged;   // a session Lux cannot read: it exists, nothing more is known
        }
    }

    /// <summary>Classify the sessions of captures whose <c>.lris</c> appeared, changed or went since the last scan (the capture itself unchanged).</summary>
    void ClassifyLumenSessions(IReadOnlyCollection<string> ids, CancellationToken cancel)
    {
        var rows = Db.Read(c => ids.Select(id => c.Rows(null, "SELECT primary_path, lris_stamp, lumen_class, missing FROM capture WHERE id=$i",
                                                        r => (Id: id, Path: r.GetString(0), Stamp: r.Str(1), Class: (LumenSessionState)r.GetInt32(2), Missing: r.GetInt64(3) != 0), ("$i", id))
                                                 .FirstOrDefault()).Where(r => r.Path is not null && !r.Missing).ToList());
        var updates = new System.Collections.Concurrent.ConcurrentBag<(string Id, LumenSessionState State, string? Stamp)>();
        Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = cancel }, r =>
        {
            string lris = Path.Combine(Path.GetDirectoryName(r.Path)!, Path.GetFileNameWithoutExtension(r.Path) + ".lris");
            if (!File.Exists(lris)) { if (r.Class != LumenSessionState.None || r.Stamp is not null) updates.Add((r.Id, LumenSessionState.None, null)); return; }
            string stamp = LrisStamp(lris);
            if (stamp == r.Stamp) return;
            try
            {
                var lri = LriFile.Load(r.Path);
                updates.Add((r.Id, ClassifyLumen(lri, lris), stamp));
                CollectAfter(lri.Data.LongLength);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        });
        if (updates.IsEmpty) return;
        Db.Write((c, tx) => { foreach (var (id, st, stamp) in updates) c.Exec(tx, "UPDATE capture SET lumen_class=$c, lris_stamp=$s WHERE id=$i", ("$c", (long)st), ("$s", stamp), ("$i", id)); });
        Changes.Changed(updates.Select(u => u.Id));
    }

    /// <summary>The photo orientation of a saved transform's 3×3 matrix (the rotation / flip part; Lumen's <c>Transform</c>).</summary>
    internal static PhotoOrientation OrientationOf(float[]? m)
    {
        if (m is null || m.Length < 5) return PhotoOrientation.Normal;
        int a = (int)MathF.Round(m[0]), b = (int)MathF.Round(m[1]), d = (int)MathF.Round(m[3]), e = (int)MathF.Round(m[4]);
        if (a * e - b * d < 0) return PhotoOrientation.Mirrored;
        return (a, b, d, e) switch
        {
            (1, 0, 0, 1) => PhotoOrientation.Normal,
            (-1, 0, 0, -1) => PhotoOrientation.Rotate180,
            (0, -1, 1, 0) => PhotoOrientation.Rotate90,
            (0, 1, -1, 0) => PhotoOrientation.Rotate270,
            _ => PhotoOrientation.Normal,
        };
    }

    internal static IReadOnlyList<PixelSize> ParseSizes(string s)
        => string.IsNullOrEmpty(s) ? Array.Empty<PixelSize>()
         : s.Split(';').Select(p => p.Split('x')).Select(p => new PixelSize(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))).ToList();

    /// <summary>The saved tone and colour values that differ from Lumen's (exposure 0, white balance as captured, sharpening 5, the rest 0).</summary>
    internal static string? AdjustmentsSummary(Lux.Store.StoredSettings rs)
    {
        var p = new List<string>();
        for (int i = 2; i < LumenRendererSettings.Count; i++)
        {
            if (!rs.Present[i] || i is LumenRendererSettings.TemperatureId or LumenRendererSettings.TintId) continue;
            float v = rs.Values[i], dflt = i == 13 ? 5f : 0f;
            if (v != dflt) p.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:+0.##;-0.##;0}", LumenRendererSettings.Names[i], v));
        }
        return p.Count == 0 ? null : string.Join(", ", p);
    }
}
