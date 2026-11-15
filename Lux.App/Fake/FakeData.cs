using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.Fake;

/// <summary>Options of the fake library (design time, screenshots, tests).</summary>
public sealed record FakeLibraryOptions
{
    /// <summary>Number of synthetic captures.</summary>
    public int Captures { get; init; } = 300;
    /// <summary>Seed of the synthetic data (same seed = same library).</summary>
    public int Seed { get; init; } = 16;
    /// <summary>Advance the jobs on a timer (false: tests call <see cref="FakeJobQueue.Tick"/>).</summary>
    public bool AutoTick { get; init; } = true;
    /// <summary>Seed a realistic mix of running, waiting, finished and failed jobs.</summary>
    public bool SeedJobs { get; init; } = true;
    /// <summary>Start with no folders (the first-run state).</summary>
    public bool Empty { get; init; }
    /// <summary>Simulated time to make a thumbnail, ms (0 = immediate).</summary>
    public int ThumbnailDelayMs { get; init; } = 25;
    /// <summary>A second indexed folder ("/fake/Shared drive/Light backup") holding some of the captures.</summary>
    public bool SecondFolder { get; init; }
    /// <summary>Every n-th capture's thumbnail is never finished (0 = none): the "being made" state stays on screen.</summary>
    public int PendingThumbnailsEvery { get; init; }
    /// <summary>Where the fake keeps its settings between runs (null = memory only).</summary>
    public string? SettingsFile { get; init; }
    /// <summary>Fixed "now" so screenshots are stable.</summary>
    public DateTime NowUtc { get; init; } = new(2026, 10, 12, 17, 5, 0, DateTimeKind.Utc);
    /// <summary>Capture changes are reported this many ms after they happen, as the library reports them in batches a moment later
    /// (0 = at once). The job queue's own events are not late.</summary>
    public int CaptureEventDelayMs { get; init; }
}

/// <summary>The synthetic capture set: names, times, lenses, ratings, cache levels and edits in realistic proportions.</summary>
internal static class FakeData
{
    public const string Root = "/fake/Pictures/Light";
    public const string Root2 = "/fake/Shared drive/Light backup";

    public static readonly string[] Tones =
        { "#7c8a96", "#a08a72", "#78907f", "#93809a", "#8791a3", "#a8937a", "#77878f", "#9a8a74", "#809484", "#988694" };

    public sealed class Capture
    {
        public required CaptureItem Item;
        public required int Index;
        public required string Folder;   // absolute directory
        public int Iso;
        public double Shutter;           // seconds
        public float? SavedFNumber;
        public float? SavedFocusM;
        public bool Removed;
        public HashSet<long> Albums = new();
        public LumenSessionInfo? Lumen;
        public bool LumenSettingsChanged;
        /// <summary>Clockwise rotation that shows the capture upright (the thumbnail JPEG is delivered unrotated).</summary>
        public int Orientation;
        /// <summary>The saved recipe's tint (the fake editor), applied to the thumbnail.</summary>
        public SkiaSharp.SKColorFilter? Tint;
    }

    public static List<Capture> Generate(FakeLibraryOptions o, out List<(long Id, string Name, DateTime Created)> albums)
    {
        var rnd = new Random(o.Seed);
        var list = new List<Capture>(o.Captures);
        albums = new() { (1, "Portfolio", o.NowUtc.AddDays(-40)), (2, "Low light", o.NowUtc.AddDays(-20)) };
        // Day structure: walk back from 12 Oct 2026, 2–15 captures a day, gaps of 1–6 days.
        var day = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Unspecified);
        int i = 0;
        while (i < o.Captures)
        {
            int perDay = Math.Min(o.Captures - i, i == 0 ? 5 : rnd.Next(2, 16));
            var t = day.AddHours(i == 0 ? 16.7 : rnd.Next(8, 17)).AddMinutes(rnd.Next(0, 50));
            for (int k = 0; k < perDay; k++, i++)
            {
                list.Add(Make(o, rnd, i, t));
                t = t.AddMinutes(rnd.Next(1, 14)).AddSeconds(rnd.Next(0, 59));
            }
            day = day.AddDays(-(i < 10 ? 3 : rnd.Next(1, 7)));
        }
        // Album membership
        foreach (var c in list)
        {
            if (c.Item.Rating >= 4 && rnd.NextDouble() < 0.6) c.Albums.Add(1);
            if (c.Item.IsStacked && rnd.NextDouble() < 0.8) c.Albums.Add(2);
        }
        return list;
    }

    static string FolderFor(DateTime t, int index, bool second)
    {
        if (second && index >= 10 && index % 7 == 3) return $"{Root2}/{t.Year}/{t:MM} {t:MMMM}";
        if (index % 23 == 19) return $"{Root}/Test charts";
        string season = t.Month switch { >= 9 and <= 11 => "Autumn", >= 6 and <= 8 => "Summer", >= 3 and <= 5 => "Spring", _ => "Winter" };
        return $"{Root}/{t.Year}/{season}";
    }

    static Capture Make(FakeLibraryOptions o, Random rnd, int i, DateTime t)
    {
        // The first ten are fixed (L16_00493 …).
        int[] focals = { 28, 28, 35, 28, 75, 28, 28, 50, 28, 150 };
        int focal = i < focals.Length ? focals[i] : rnd.NextDouble() switch { < 0.62 => 28, < 0.74 => 35, < 0.82 => 50, < 0.93 => 75, _ => 150 };
        int stack = (i == 2 || (i >= 10 && rnd.NextDouble() < 0.09)) ? 4 : 1;
        int rating = i < 10 ? new[] { 4, 3, 5, 0, 2, 0, 3, 4, 0, 0 }[i] : rnd.NextDouble() switch { < 0.55 => 0, < 0.65 => 1, < 0.75 => 2, < 0.87 => 3, < 0.95 => 4, _ => 5 };
        CacheLevel level = i < 10
            ? new[] { CacheLevel.Full, CacheLevel.Linear13, CacheLevel.Full, CacheLevel.Linear13, CacheLevel.Linear13, CacheLevel.None, CacheLevel.Linear13, CacheLevel.Full, CacheLevel.None, CacheLevel.Linear13 }[i]
            : rnd.NextDouble() switch { < 0.08 => CacheLevel.None, < 0.12 => CacheLevel.Geometry, < 0.9 => CacheLevel.Linear13, _ => CacheLevel.Full };
        var edits = EditSummary.None;
        bool lumenSession = i == 7 || (i >= 10 && rnd.NextDouble() < 0.12);
        if (i == 0) edits = new EditSummary("lux", 4, EditSource.Lux, 2, true, false);
        else if (i == 7) edits = new EditSummary("lumen-import", 2, EditSource.Lumen, 2, true, false);
        else if (i >= 10 && level != CacheLevel.None && rnd.NextDouble() < 0.07)
            edits = new EditSummary("lux", rnd.Next(1, 9), EditSource.Lux, 2, true, rnd.NextDouble() < 0.05);
        bool missing = i >= 10 && rnd.NextDouble() < 0.012;
        bool unreadable = i == 41;
        long cacheBytes = level switch
        {
            CacheLevel.Geometry => (14 + rnd.Next(0, 4)) * 1_000_000L,
            CacheLevel.Linear13 => (66 + rnd.Next(0, 6)) * 1_000_000L,
            CacheLevel.Full => (248 + rnd.Next(0, 30)) * 1_000_000L,
            _ => 0,
        };
        string name = $"L16_{493 + i:00000}";
        string folder = FolderFor(t, i, o.SecondFolder);
        int[] isos = { 100, 100, 100, 125, 160, 200, 400, 800 };
        double[] shutters = { 1 / 1000.0, 1 / 800.0, 1 / 640.0, 1 / 500.0, 1 / 400.0, 1 / 320.0, 1 / 250.0, 1 / 60.0 };
        var item = new CaptureItem
        {
            Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i * 7919 + 13))).ToLowerInvariant(),
            Path = $"{folder}/{name}.lri",
            Name = name,
            CaptureTime = unreadable ? null : t,
            FocalLengthMm = focal,
            StackFrames = stack,
            ReferenceModule = focal >= 75 ? "B4" : "A1",
            ModuleCount = focal >= 75 ? 10 : 10,
            AllInFocusFNumber = 16f,
            IsTelephoto = focal >= 75,
            FileSize = (stack > 1 ? 400 : 120) * 1_000_000L + rnd.Next(0, 9_000_000),
            FileModifiedUtc = t.ToUniversalTime(),
            AddedUtc = o.NowUtc.AddDays(-30),
            Rating = rating,
            RatingSource = rating > 0 ? RatingSource.Capture : RatingSource.None,
            CacheLevel = unreadable ? CacheLevel.None : level,
            CacheBytes = unreadable ? 0 : cacheBytes,
            DepthState = unreadable ? DepthState.Failed : level == CacheLevel.None ? DepthState.NotAnalysed : DepthState.Analysed,
            Edits = edits,
            HasLumenSession = lumenSession,
            HasThumbnail = !unreadable,
            ThumbnailCurrent = true,
            Missing = missing,
            Unreadable = unreadable,
            Error = unreadable ? "This file is not a complete Light L16 capture (it ends early)." : null,
        };
        // where the photo's data lives: beside it when it has any; a few on a read-only drive (local store); one rating not saved anywhere;
        // one superseded local copy kept aside after a conflict
        bool hasData = item.Rating > 0 || item.Edits.IsEdited || item.CacheLevel != CacheLevel.None;
        bool local = i == 3 || (i >= 10 && rnd.NextDouble() < 0.04);
        var location = !hasData ? ContextLocation.None : local ? ContextLocation.LocalStore : ContextLocation.BesideCapture;
        item = item with { ContextLocation = location };
        if (i == 6) item = item with { RatingSource = RatingSource.Library, ContextLocation = ContextLocation.None };
        if (i == 1) item = item with { SupersededContextPath = $"/fake/Lux/contexts-superseded/{item.Id[..16]}-2026-10-11T0930Z" };
        var c = new Capture
        {
            Item = item, Index = i, Folder = folder, Orientation = i == 4 ? 90 : i == 9 ? 270 : (i >= 10 && i % 29 == 7) ? 90 : 0,
            Iso = stack > 1 ? 800 : isos[rnd.Next(isos.Length)],
            Shutter = stack > 1 ? 1 / 15.0 : shutters[rnd.Next(shutters.Length)],
        };
        if (edits.HasSettings || i == 0) { c.SavedFNumber = i == 0 ? 2.0f : 2.8f; c.SavedFocusM = i == 0 ? 4.56f : 2.1f; }
        if (lumenSession)
        {
            bool depth = i == 7 || rnd.NextDouble() < 0.12;
            c.Lumen = new LumenSessionInfo($"{folder}/{name}.lris", 26_000_000, t.ToUniversalTime().AddDays(2), depth, i == 7);
            c.LumenSettingsChanged = depth || rnd.NextDouble() < 0.2;
        }
        var e = c.Item.Edits;
        if (c.SavedFNumber is float fn)
            e = e with
            {
                SavedFNumber = fn, SavedFocusMm = c.SavedFocusM * 1000,
                AdjustmentsSummary = e.Source == EditSource.Lumen ? "exposure +0.30, contrast 12" : i == 0 ? "exposure +0.70" : null,
            };
        bool tele = focal >= 75;
        c.Item = c.Item with
        {
            Edits = e,
            Iso = c.Iso, ExposureTimeSeconds = c.Shutter, Gain = c.Iso / 100f,
            HeldOrientation = c.Orientation switch { 90 => PhotoOrientation.Rotate90, 270 => PhotoOrientation.Rotate270, _ => PhotoOrientation.Normal },
            Width = tele ? 7728 : 8320, Height = tele ? 5796 : 6240,
            LevelSizes = tele
                ? new PixelSize[] { new(7728, 5796), new(3864, 2898), new(1932, 1449), new(966, 724), new(483, 362) }
                : new PixelSize[] { new(8320, 6240), new(4160, 3120), new(2080, 1560), new(1040, 780), new(520, 390) },
            LumenSession = c.Lumen == null ? LumenSessionState.None : c.Lumen.HasDepthEdits ? LumenSessionState.DepthEdited
                : c.LumenSettingsChanged ? LumenSessionState.SettingsChanged : LumenSessionState.OpenedUnchanged,
        };
        return c;
    }

}
