using System.Text.Json;
using Lux.App.Fake;
using Lux.Library;
using Lux.Library.Camera;

namespace Lux.App.Services;

/// <summary>Folder picker and file-manager access; the main window provides the real one, tests a scripted one.</summary>
public interface IPlatformServices
{
    /// <summary>Ask for a folder; null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string title);
    /// <summary>Show a file in the system's file manager (its folder when the manager cannot select files).</summary>
    void Reveal(string path);
}

/// <summary>The app's own small configuration file in its data folder (what must be known before the library opens).</summary>
public sealed class AppConfig
{
    public string? LibraryFile { get; set; }

    public static AppConfig Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Everything the view models use, built by <see cref="CompositionRoot"/>.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public ILibrary Library { get; }
    public required ILumenScanner Lumen { get; init; }
    public required IDeviceStatus Device { get; init; }
    /// <summary>The camera (the Camera page): the real service over libgphoto2, the fake camera of <c>--fake</c>, or "not available".</summary>
    public ICameraService Camera { get; init; } = new UnavailableCameraService();
    /// <summary>The app's own updates: the real service in an installed copy, otherwise "not available".</summary>
    public IUpdateService Updates { get; init; } = new NoUpdateService();
    public required IPlatformServices Platform { get; set; }
    public required string AppDataDir { get; init; }
    public required bool IsFake { get; init; }
    /// <summary>The Develop page's editor (the fake, the contract adapter, or "not available").</summary>
    public Develop.IDevelopService Develop { get; set; } = new Develop.UnavailableDevelopService("Editing needs a newer library.");
    /// <summary>How the viewer swaps a changed picture in (Settings → Viewer).</summary>
    public ViewerPreferences ViewerPrefs { get; } = new();
    public ThumbnailCache Thumbnails { get; }
    /// <summary>What the export and animate panels were last set to in this run of the app (folder, formats, size, …): they reopen with it.</summary>
    public PanelMemory PanelMemory { get; } = new();
    public string ConfigFile => Path.Combine(AppDataDir, "lux-app.json");

    public AppServices(ILibrary library)
    {
        Library = library;
        Thumbnails = new ThumbnailCache(library);
    }

    public async ValueTask DisposeAsync()
    {
        Thumbnails.Clear();
        try { Camera.Dispose(); } catch (Exception) { /* closing the camera must not stop the library from closing */ }
        await Library.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Builds the services: the fake library (<c>--fake</c>) or the real one, with the app-data folder override.</summary>
public static class CompositionRoot
{
    public static AppServices Create(AppOptions o, IPlatformServices platform, Action<string>? log = null)
    {
        string appData = o.AppDataDir is { Length: > 0 } d ? Path.GetFullPath(d) : LibraryPaths.DefaultDataFolder;
        if (o.Fake)
        {
            var fake = new FakeLibrary(new FakeLibraryOptions
            {
                Captures = o.FakeCaptures, Empty = o.FakeEmpty,
                SettingsFile = Path.Combine(appData, "fake-settings.json"),
            });
            if (o.StartPaused) fake.Jobs.PauseAll();
            // a fake camera with 38 new captures (real sizes; the copies of an import go where the panel says)
            var fakeCamera = new CameraService(fake, new FakeMtpBackend(new FakeCamera(new FakeCameraOptions { FileListMs = 1500, DetailsMs = 4, BytesPerSecond = 60_000_000 }, fake)),
                                               new CameraServiceOptions
                                               {
                                                   PreviewFolder = Path.Combine(appData, "camera-previews"), ListingFolder = Path.Combine(appData, "camera-listings"), ShrinkPreview = CameraPreviews.Shrink, Log = log,
                                                   DefaultDestination = Path.Combine(appData, "fake-camera-imports"),   // never the user's Pictures folder
                                               });
            return new AppServices(fake)
            {
                Lumen = new ContractLumenScanner(fake), Device = new CameraDeviceStatus(fakeCamera), Camera = fakeCamera, Platform = platform, AppDataDir = appData, IsFake = true,
                Develop = new FakeDevelopService(fake),
            };
        }

        var config = AppConfig.Load(Path.Combine(appData, "lux-app.json"));
        string db = o.LibraryFile ?? config.LibraryFile ?? Path.Combine(appData, "library.luxlib");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(db))!);
        var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = db, StartWorkers = !o.StartPaused, Log = log });
        // the camera over libgphoto2 (read-only: lists, reads previews, copies); thumbnails of its previews are kept in the app's data folder
        ICameraService camera;
        try
        {
            camera = new CameraService(lib, Lux.Engine.Mtp.MtpFactory.Backend,
                                       new CameraServiceOptions { PreviewFolder = Path.Combine(appData, "camera-previews"), ListingFolder = Path.Combine(appData, "camera-listings"), ShrinkPreview = CameraPreviews.Shrink, Log = log });
        }
        catch (Exception e) when (e is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException)
        {
            log?.Invoke("camera support: " + e);
            camera = new UnavailableCameraService("Lux could not start its camera support on this computer, so importing from the camera is not available. Copy the captures to a folder and add the folder in the Library.");
        }
        return new AppServices(lib)
        {
            Lumen = new ContractLumenScanner(lib), Device = new CameraDeviceStatus(camera), Camera = camera,
            Platform = platform, AppDataDir = appData, IsFake = false,
            Updates = new VelopackUpdateService(),
            Develop = Develop.ContractDevelopService.Create(lib),
        };
    }
}

/// <summary>The export and animate panels' last choices for this run of the app (destination memory per session).</summary>
public sealed class PanelMemory
{
    public Lux.Library.ExportOptions? Export { get; set; }
    /// <summary>The size choice's value (level, or -1 = custom) and the custom long edge.</summary>
    public int ExportSizeChoice { get; set; }
    public int? ExportCustomEdge { get; set; }
    public int ExportDepthChoice { get; set; }
    public double ExportFNumber { get; set; } = 2.8;
    public Lux.Library.AnimationOptions? Animation { get; set; }
}
