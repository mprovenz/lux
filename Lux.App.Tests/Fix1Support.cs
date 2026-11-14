using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lux.Library;

namespace Lux.App.Tests;

/// <summary>Set-up of the real-data tests: linked libraries and app data under <see cref="Fix1.Root"/>, rebuilt clean on every run.
/// Originals are only linked to (never written next to); the source data is never touched (copies only).</summary>
public static class Fix1
{
    static string Here([CallerFilePath] string file = "") => file;

    public static string Workspace => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
    /// <summary>The real-data test folder in the workspace; outside the workspace (another machine) the test output folder's <c>fix1</c>.</summary>
    public static string Root
    {
        get
        {
            var dir = Directory.Exists(Path.Combine(Workspace, "scratch")) ? Path.Combine(Workspace, "scratch", "fix1") : Path.Combine(AppContext.BaseDirectory, "fix1");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string Shots => Path.Combine(Root, "shots");
    public static string Lumen2025(string day, string name) => Path.Combine(Workspace, "assets", "lumen-library-2025", day, name);

    /// <summary>The sample capture <paramref name="name"/> (e.g. L16_00493) wherever the local capture collection keeps it.</summary>
    public static string Corpus(string name)
    {
        foreach (var root in new[] { Paths.Captures, Path.Combine(Workspace, "assets") })
        {
            if (!Directory.Exists(root)) continue;
            var hit = Directory.EnumerateFiles(root, name + ".lri", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                               .FirstOrDefault(p => !p.Contains("LuxImageContext", StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        throw new FileNotFoundException(name);
    }

    /// <summary>Remove a folder under <see cref="Root"/> without following links (a link is removed, never its target).</summary>
    public static void Wipe(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("refusing to wipe outside scratch/fix1: " + full);
        if (!Directory.Exists(full)) return;
        foreach (var e in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (e.LinkTarget != null || e is FileInfo) e.Delete();
            else Wipe(e.FullName);
        }
        Directory.Delete(full);
    }

    /// <summary>A new folder of links (<c>lib/&lt;name&gt;</c> under <see cref="Root"/>) to <paramref name="files"/> (each .lri, plus its .lris when asked).</summary>
    public static string MakeLib(string name, IEnumerable<string> files, bool withSessions = false)
    {
        var dir = Path.Combine(Root, "lib", name);
        Wipe(dir);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(f)), f);
            var lris = Path.ChangeExtension(f, ".lris");
            if (withSessions && File.Exists(lris)) File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(lris)), lris);
        }
        return dir;
    }

    /// <summary>A new app-data folder (<c>appdata/&lt;name&gt;</c> under <see cref="Root"/>).</summary>
    public static string MakeAppData(string name)
    {
        var dir = Path.Combine(Root, "appdata", name);
        Wipe(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A library in a new app-data folder with <paramref name="folder"/> added and scanned.</summary>
    public static async Task<ILibrary> OpenLibraryAsync(string appData, string folder, Action<string>? log = null)
    {
        var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = Path.Combine(appData, "library.luxlib"), Log = log, MakeThumbnailsAfterScan = false });
        await lib.AddFolderAsync(folder);
        var sw = Stopwatch.StartNew();
        while (lib.GetFolders().Any(f => f.Scanning) && sw.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(100);
        return lib;
    }

    public static CaptureItem Item(ILibrary lib, string name) =>
        lib.Query(new CaptureQuery { Limit = 1000 }).Items.First(i => i.Name == name);

    /// <summary>Mean absolute difference per channel value of two RGBA8 frames of one size (alpha ignored).</summary>
    public static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.NaN;
        long s = 0, n = 0;
        for (int i = 0; i < a.Length; i += 4) { s += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]); n += 3; }
        return (double)s / n;
    }

    /// <summary>A frame as a binary PPM (for looking at it; converted to PNG outside).</summary>
    public static void SavePpm(PreviewFrame f, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{f.Width} {f.Height}\n255\n");
        fs.Write(head);
        var row = new byte[f.Width * 3];
        for (int y = 0; y < f.Height; y++)
        {
            for (int x = 0; x < f.Width; x++) { int i = (y * f.Width + x) * 4; row[x * 3] = f.Rgba[i]; row[x * 3 + 1] = f.Rgba[i + 1]; row[x * 3 + 2] = f.Rgba[i + 2]; }
            fs.Write(row);
        }
    }

    public static void Status(string line)
    {
        Directory.CreateDirectory(Root);
        File.AppendAllText(Path.Combine(Root, "status.txt"), $"{DateTime.Now:HH:mm:ss} {line}\n");
    }
}
