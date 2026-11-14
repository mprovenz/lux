namespace Lux.App.Services;

/// <summary>Command-line options of the app.</summary>
public sealed record AppOptions
{
    /// <summary>Use the in-memory fake library (<c>--fake [captures]</c>).</summary>
    public bool Fake { get; init; }
    public int FakeCaptures { get; init; } = 300;
    /// <summary>The fake starts with no folders (first-run state).</summary>
    public bool FakeEmpty { get; init; }
    /// <summary>The app's data folder (<c>--appdata DIR</c>, or LUX_APPDATA, or LUX_APP_DATA); null = the per-user default.</summary>
    public string? AppDataDir { get; init; }
    /// <summary>A library database file to open instead of the one in the data folder (<c>--library FILE</c>).</summary>
    public string? LibraryFile { get; init; }
    /// <summary>Add this folder to the library at start (<c>--add-folder DIR</c>).</summary>
    public string? AddFolder { get; init; }
    /// <summary>Start with the job queue paused (<c>--paused</c>).</summary>
    public bool StartPaused { get; init; }
    /// <summary>Render every page to PNG files in this folder and exit (<c>--screenshot DIR</c>).</summary>
    public string? ScreenshotDir { get; init; }
    /// <summary>Open the control gallery (<c>--gallery</c>).</summary>
    public bool Gallery { get; init; }

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        // LUX_APPDATA (the app's), else LUX_APP_DATA (the library's and the tests' isolation variable); --appdata wins over both
        var o = new AppOptions
        {
            AppDataDir = Environment.GetEnvironmentVariable("LUX_APPDATA") is { Length: > 0 } env ? env
                : Environment.GetEnvironmentVariable("LUX_APP_DATA") is { Length: > 0 } env2 ? env2 : null,
        };
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--fake":
                    o = o with { Fake = true };
                    if (i + 1 < args.Count && int.TryParse(args[i + 1], out int n)) { o = o with { FakeCaptures = n }; i++; }
                    break;
                case "--fake-empty": o = o with { Fake = true, FakeEmpty = true }; break;
                case "--appdata": o = o with { AppDataDir = Next() }; break;
                case "--library": o = o with { LibraryFile = Next() }; break;
                case "--add-folder": o = o with { AddFolder = Next() }; break;
                case "--paused": o = o with { StartPaused = true }; break;
                case "--screenshot": o = o with { ScreenshotDir = Next() }; break;
                case "--gallery": o = o with { Gallery = true }; break;
            }
        }
        return o;
    }
}
