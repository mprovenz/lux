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
        // the problem log in the app's data folder: from here on, an exception that nothing handles is recorded there
        try { CrashLog.Start(CompositionRoot.AppDataFolder(Options)); } catch (Exception) { /* no data folder: the app says so when it opens the library */ }
        CrashLog.InstallProcessHandlers();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
