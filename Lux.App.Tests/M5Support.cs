using System.Runtime.CompilerServices;

namespace Lux.App.Tests;

/// <summary>Where the camera tests write: an output folder in the workspace (LUX_M5_SCRATCH overrides; the test output folder
/// when the tests run outside the workspace). Imports in tests go only below it.</summary>
public static class M5
{
    static string Here([CallerFilePath] string file = "") => file;

    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LUX_M5_SCRATCH");
            string dir;
            if (!string.IsNullOrEmpty(env)) dir = env;
            else
            {
                var workspace = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
                dir = Directory.Exists(Path.Combine(workspace, "scratch")) ? Path.Combine(workspace, "scratch", "m5") : Path.Combine(AppContext.BaseDirectory, "m5");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Shots => Made(Path.Combine(Root, "shots"));

    public static string Made(string dir) { Directory.CreateDirectory(dir); return dir; }

    /// <summary>A fresh folder under <see cref="Root"/> (emptied first).</summary>
    public static string Fresh(string name)
    {
        var dir = Path.Combine(Root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Real-camera tests run only with LUX_M5_REAL=1 (the Light L16 connected over USB).</summary>
    public static bool Real => Environment.GetEnvironmentVariable("LUX_M5_REAL") == "1";

    /// <summary>Append a line to &lt;file&gt; under <see cref="Root"/> (results of the real-camera runs).</summary>
    public static void Record(string file, string line)
    {
        File.AppendAllText(Path.Combine(Root, file), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
    }
}
