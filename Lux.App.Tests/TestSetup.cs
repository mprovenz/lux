using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lux.App.Controls;
using Lux.App.Fake;
using Lux.App.Services;
using Lux.App.Tests;
using Lux.App.ViewModels;
using Lux.App.Views;
using Lux.Library;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// One headless application, font manager and dispatcher for the whole assembly. Avalonia's default (a fresh application per
// test) disposes the test's FontManager and then runs the jobs still queued on the dispatcher; a job a background thread (library events,
// thumbnails, jobs) posted in that window shapes text against the disposed font manager ("The given key 'fonts:SystemFonts' was not present
// in the dictionary") and fails whichever test is ending. Tests close their windows; Harness.CreateAsync closes any a failed test left open.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Lux.App.Tests;

/// <summary>The real <see cref="App"/> (its resources and styles) on the headless platform with Skia rendering, so windows can be
/// captured to PNG.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => TestWindows.Track());
}

/// <summary>The windows the tests have open (the application lives for the whole assembly, so a window a failed test did not close would keep
/// rendering in the tests after it). Tooltips are off in test windows: the tests press at computed points without moving the pointer there
/// first, and when the UI thread is slow (a loaded machine) the 400 ms show delay passes while the pointer rests, the tooltip opens under
/// the next press and takes it (a press raised on a ToolTip makes a selection test fail).</summary>
public static class TestWindows
{
    static readonly HashSet<Window> Open = new();
    static bool _tracking;

    internal static void Track()
    {
        if (_tracking) return;
        _tracking = true;
        Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (s, _) => { if (s is Window w) { Open.Add(w); ToolTip.SetServiceEnabled(w, false); } });
        Window.WindowClosedEvent.AddClassHandler(typeof(Window), (s, _) => { if (s is Window w) Open.Remove(w); });
    }

    /// <summary>Windows closed because an earlier test left them open (for debugging a run).</summary>
    public static int StrayClosed { get; private set; }

    /// <summary>Close every window still open and let the queued work run.</summary>
    public static void CloseStray()
    {
        foreach (var w in Open.ToArray())
        {
            StrayClosed++;
            try { w.Close(); } catch (Exception e) { Console.Error.WriteLine($"closing a stray window failed: {e.Message}"); }
            Open.Remove(w);
        }
        Dispatcher.UIThread.RunJobs();
    }
}

public static class FrameExtensions
{
    /// <summary>Render a frame and drop it at once. <c>CaptureRenderedFrame</c> returns a fresh copy of the frame each time (several MB of native
    /// memory that only a finalizer would free otherwise).</summary>
    public static void RenderFrame(this TopLevel top) => top.CaptureRenderedFrame()?.Dispose();
}

/// <summary>Where screenshots and measurements go.</summary>
public static class Paths
{
    static string Here([CallerFilePath] string file = "") => file;
    /// <summary>The folder, created when missing (tests run outside the workspace fall back to the test output folder, which has none of the
    /// output folders yet — a report or trace written there must not fail).</summary>
    static string Made(string dir) { Directory.CreateDirectory(dir); return dir; }

    /// <summary>The folder with real L16 captures for the tests that need them. Set <c>LUX_TEST_CAPTURES</c>. Without it, those tests
    /// find no capture and skip or fail.</summary>
    public static string Captures => Environment.GetEnvironmentVariable("LUX_TEST_CAPTURES") ?? "";

    /// <summary>The <c>lux-light</c> program that the comparison tests run. Set <c>LUX_LIGHT</c> to its path.</summary>
    public static string LuxLight => Environment.GetEnvironmentVariable("LUX_LIGHT") ?? "lux-light";

    /// <summary>The workspace's UI output folder when the tests run from it, else the test output folder.</summary>
    public static string UiScratch
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LUX_UI_SCRATCH");
            if (!string.IsNullOrEmpty(env)) return Made(env);
            var workspace = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
            var scratch = Path.Combine(workspace, "scratch", "m1", "ui");
            return Made(Directory.Exists(Path.Combine(workspace, "scratch")) ? scratch : Path.Combine(AppContext.BaseDirectory, "ui"));
        }
    }

    public static string Shots => Environment.GetEnvironmentVariable("LUX_SHOTS_DIR") is { Length: > 0 } s ? s : Path.Combine(UiScratch, "shots");
    public static string AppData(string name) => Path.Combine(UiScratch, "appdata", name);

    /// <summary>A second UI output folder in the workspace (LUX_M2_SCRATCH overrides), else under the test output folder.</summary>
    public static string M2Scratch
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LUX_M2_SCRATCH");
            if (!string.IsNullOrEmpty(env)) return Made(env);
            var workspace = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
            return Made(Directory.Exists(Path.Combine(workspace, "scratch")) ? Path.Combine(workspace, "scratch", "m2", "ui") : Path.Combine(AppContext.BaseDirectory, "m2ui"));
        }
    }

    public static string M2Shots => Path.Combine(M2Scratch, "shots");

    /// <summary>A third UI output folder in the workspace (LUX_M3_SCRATCH overrides), else under the test output folder.</summary>
    public static string M3Scratch
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LUX_M3_SCRATCH");
            if (!string.IsNullOrEmpty(env)) return Made(env);
            var workspace = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
            return Made(Directory.Exists(Path.Combine(workspace, "scratch")) ? Path.Combine(workspace, "scratch", "m3", "ui") : Path.Combine(AppContext.BaseDirectory, "m3ui"));
        }
    }

    public static string M3Shots => Path.Combine(M3Scratch, "shots");
}

/// <summary>A scripted folder picker and file manager.</summary>
public sealed class TestPlatform : IPlatformServices
{
    public string? NextFolder { get; set; }
    public List<string> Revealed { get; } = new();
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(NextFolder);
    public void Reveal(string path) => Revealed.Add(path);
}

/// <summary>The main window on a fake library, with helpers to let background work settle and to capture frames.</summary>
public sealed class Harness : IAsyncDisposable
{
    public required FakeLibrary Lib { get; init; }
    public required AppServices Services { get; init; }
    public required MainViewModel Vm { get; init; }
    public required MainWindow Window { get; init; }
    public required TestPlatform Platform { get; init; }
    public LibraryViewModel Library => Vm.Library;

    public static async Task<Harness> CreateAsync(FakeLibraryOptions? options = null, double width = 1440, double height = 900, FakeDevelopOptions? develop = null,
                                                  Func<FakeLibrary, ICameraService>? camera = null)
    {
        TestWindows.CloseStray();
        var lib = new FakeLibrary((options ?? new FakeLibraryOptions()) with { AutoTick = false });
        var platform = new TestPlatform();
        // the camera tests give a camera service over the fake camera; every other test has none
        var cam = camera?.Invoke(lib);
        var s = new AppServices(lib)
        {
            Lumen = new ContractLumenScanner(lib), Device = cam != null ? new CameraDeviceStatus(cam) : new NoDeviceStatus(), Camera = cam ?? new UnavailableCameraService(),
            Platform = platform,
            AppDataDir = Paths.AppData("tests"), IsFake = true,
            Develop = new FakeDevelopService(lib, develop ?? new FakeDevelopOptions { Timings = FakeTimings.Fast, OpenDelayMs = 0, SaveDelayMs = 20 }),
        };
        ThumbnailView.Cache = s.Thumbnails;
        var vm = new MainViewModel(s);
        var win = new MainWindow { DataContext = vm, Width = width, Height = height };
        win.Show();
        await vm.InitializeAsync();
        var h = new Harness { Lib = lib, Services = s, Vm = vm, Window = win, Platform = platform };
        await h.SettleAsync();
        return h;
    }

    public BrowseView Browse => Window.FindControl<LibraryView>("LibraryPage")!.FindControl<BrowseView>("Browse")!;

    /// <summary>Deliver the library's events, run the UI's queued work, lay out and render, until <paramref name="until"/> holds and no
    /// thumbnail is in flight (or the timeout passes).</summary>
    public async Task SettleAsync(Func<bool>? until = null, int timeoutMs = 8000, bool waitThumbnails = true)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Lib.FlushEventsAsync();
            Dispatcher.UIThread.RunJobs();
            Window.RenderFrame();
            Dispatcher.UIThread.RunJobs();
            bool ok = (until?.Invoke() ?? true) && !Vm.Library.IsLoading && (!waitThumbnails || Services.Thumbnails.InFlight == 0);
            quiet = ok ? quiet + 1 : 0;
            if (quiet >= 3) break;
            await Task.Delay(15);
        }
    }

    public string Shot(string name) => Capture(Window, name);
    /// <summary>A screenshot into <c>Paths.M2Shots</c>.</summary>
    public string ShotM2(string name) => Capture(Window, name, Paths.M2Shots);
    /// <summary>A screenshot into <c>Paths.M3Shots</c>.</summary>
    public string ShotM3(string name) => Capture(Window, name, Paths.M3Shots);

    public static string Capture(TopLevel top, string name, string? folder = null)
    {
        Dispatcher.UIThread.RunJobs();
        // `using` keeps the frame reachable until the PNG is written: Avalonia's WriteableBitmap.Save encodes an SKImage that only borrows the
        // bitmap's pixels, so a frame left unreferenced during the encode could be finalized mid-write (a native use-after-free that crashed
        // the test host in sk_image_encode).
        using var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        folder ??= Paths.Shots;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        frame.Save(path);
        return path;
    }

    public CaptureItemViewModel Item(string name) => Library.Items.First(i => i.Name == name);

    public async Task SelectScopeAsync(string key)
    {
        Library.SelectedNode = Library.Tree.First(n => n.Key == key);
        await SettleAsync();
    }

    /// <summary>A fixed scope and selection for the screenshots: 2026 · Autumn, three captures selected, the first one's details.</summary>
    public async Task MockupSelectionAsync()
    {
        await SelectScopeAsync(Library.Tree.First(n => n.Label == "Autumn").Key);
        Library.Click(Item("L16_00493"), false, false);
        Library.Click(Item("L16_00495"), true, false);
        Library.Click(Item("L16_00500"), true, false);
        Library.Click(Item("L16_00493"), true, false);   // toggles off …
        Library.Click(Item("L16_00493"), true, false);   // … and on again: the cursor (details) is on L16_00493
        await SettleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        await Vm.DisposeAsync();
    }
}

public static class JobsExtensions
{
    public static IEnumerable<JobInfo> Of(this IJobQueue q, JobKind kind) => q.GetJobs().Where(j => j.Kind == kind);
}
