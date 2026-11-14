using System.Globalization;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>The app's wording and number formats in one place (plain language).</summary>
public static class Text
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static string Bytes(long b) => b switch
    {
        >= 1L << 40 => (b / (double)(1L << 40)).ToString("0.0", En) + " TB",
        >= 1L << 30 => (b / (double)(1L << 30)).ToString(b >= 100L << 30 ? "0" : "0.0", En) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0", En) + " MB",
        >= 1L << 10 => (b / (double)(1L << 10)).ToString("0", En) + " KB",
        _ => b + " B",
    };

    /// <summary>Decimal megabytes ("70 MB", "270 MB").</summary>
    public static string Mb(long b) => b >= 1_000_000_000 ? (b / 1e9).ToString("0.0", En) + " GB" : Math.Max(1, (int)Math.Round(b / 1e6)) + " MB";

    public static string Stars(int rating) => rating <= 0 ? "" : new string('★', Math.Min(rating, 5));
    public static string StarsOutOfFive(int rating) => new string('★', Math.Clamp(rating, 0, 5)) + new string('☆', 5 - Math.Clamp(rating, 0, 5));

    public static string ShortDateTime(DateTime? t) => t is { } v ? v.ToString("d MMM HH:mm", En) : "—";
    public static string LongDateTime(DateTime? t) => t is { } v ? v.ToString("d MMM yyyy HH:mm", En) : "No capture time";
    public static string Day(DateOnly? d) => d is { } v ? v.ToString("d MMMM yyyy", En) : "No capture time";
    public static string Time(DateTime t) => t.ToLocalTime().ToString("HH:mm", En);

    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("#,0", En)} {many}";
    public static string Number(int n) => n.ToString("#,0", En);

    public static string ShortName(string name) => name.StartsWith("L16_", StringComparison.Ordinal) ? name[4..] : name;

    public static string Lens(int mm) => mm > 0 ? $"{mm} mm" : "—";

    /// <summary>Grid cell corner tag: FULL / 13 / GEO / ··· .</summary>
    public static string CacheTag(CacheLevel l) => l switch { CacheLevel.Full => "FULL", CacheLevel.Linear13 => "13", CacheLevel.Geometry => "GEO", _ => "···" };

    /// <summary>Timeline card: FULL / 13 MP / depth only / none.</summary>
    public static string CacheWord(CacheLevel l) => l switch { CacheLevel.Full => "FULL", CacheLevel.Linear13 => "13 MP", CacheLevel.Geometry => "depth only", _ => "none" };

    public static string CacheLevelName(CacheLevel l) => l switch
    {
        CacheLevel.Full => "Full detail",
        CacheLevel.Linear13 => "Standard (13 MP working copy)",
        CacheLevel.Geometry => "Depth only (no working copy)",
        _ => "Nothing kept",
    };

    /// <summary>List and details: "Full · 270 MB", "13 MP · 70 MB", "Depth only · 15 MB", "none".</summary>
    public static string CachedRender(CacheLevel l, long bytes) => l switch
    {
        CacheLevel.Full => "Full · " + Mb(bytes),
        CacheLevel.Linear13 => "13 MP · " + Mb(bytes),
        CacheLevel.Geometry => "Depth · " + Mb(bytes),
        _ => "none",
    };

    public static string Edits(EditSummary e) => e.Source switch
    {
        EditSource.Lux => $"{e.OperationCount} · Lux",
        EditSource.Lumen => $"{e.OperationCount} · Lumen",
        _ => "",
    };

    public static string JobKindLabel(JobKind k) => k switch
    {
        JobKind.Thumbnail => "Thumbnails",
        JobKind.Analysis => "Analysis",
        JobKind.CacheBuild => "Working copy",
        JobKind.CacheUpgrade => "Full detail",
        JobKind.CacheRelease => "Release full detail",
        JobKind.Export => "Export",
        JobKind.Import => "Camera import",
        JobKind.LumenImport => "Lumen edits",
        JobKind.FolderScan => "Folder scan",
        JobKind.ContextStoreMove => "Moving photo data",
        JobKind.Animation => "Animation",
        _ => k.ToString(),
    };

    public static string Duration(TimeSpan d) => d.TotalSeconds < 60 ? $"{Math.Max(1, (int)Math.Round(d.TotalSeconds))} s"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{(int)d.TotalHours} h {d.Minutes} min";

    public static string Percent(double p) => $"{(int)Math.Round(p * 100)} %";

    public static string FNumber(float f) => "f/" + f.ToString("0.0", En);
    public static string Metres(float m) => m.ToString("0.00", En) + " m";

    public static string GiB(double gb) => gb.ToString(gb >= 10 ? "0" : "0.#", En) + " GB";

    /// <summary>Puts an exception into words that a user can act on. The messages of the library are already plain, so they show as they are.
    /// For other exceptions, the text says the kind of problem. It never shows the raw exception text.</summary>
    public static string Plain(Exception e) => e switch
    {
        AggregateException { InnerExceptions.Count: 1 } a => Plain(a.InnerExceptions[0]),
        LibraryException => e.Message,
        FileNotFoundException or DirectoryNotFoundException => "A file or folder it needs is no longer there. Check that its drive is connected, then try again.",
        UnauthorizedAccessException => "Lux is not allowed to read or write there. Choose another folder, or allow Lux to use this one.",
        IOException io when (OperatingSystem.IsWindows() ? (io.HResult & 0xFFFF) is 112 or 39 : (io.HResult & 0xFFFF) == 28) =>
            "The drive is full. Free some space, or choose a folder on another drive, and try again.",
        IOException => "A file could not be read or written. Check that its drive is connected and has space, then try again.",
        NotSupportedException => "This is not available in this version of Lux.",
        _ => "Something unexpected stopped it. Try again; if it happens again, restart Lux.",
    };
}
