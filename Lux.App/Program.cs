using Avalonia;
using Lux.App.Services;

namespace Lux.App;

internal static class Program
{
    /// <summary>The parsed command line (read by <see cref="App"/>).</summary>
    public static AppOptions Options { get; private set; } = new();

    // Avalonia: nothing may touch the UI toolkit before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // Installed copies (Windows installer, Linux AppImage): the installer and the updater start the app with their own
        // arguments (first run, after an update, on uninstall); this handles them and returns at once otherwise. It must run
        // before anything else. In a plain archive or a development build it does nothing.
        Velopack.VelopackApp.Build().Run();
        Options = AppOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
