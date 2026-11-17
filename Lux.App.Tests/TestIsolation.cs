using System.Runtime.CompilerServices;

#pragma warning disable CA2255 // a module initializer in a test assembly is the point: it runs before any test

namespace Lux.App.Tests;

/// <summary>Test isolation: the app tests never resolve the per-user Lux app-data folder (the real library on that machine). Before any
/// test runs, <c>LUX_APP_DATA</c> defaults to <c>&lt;UiScratch&gt;/appdata/default</c> (only a path; the tests give their libraries explicit
/// folders anyway) and <c>LUX_FORBID_DEFAULT_APP_DATA=1</c> makes any code path that would still reach the per-user folder throw.</summary>
static class TestIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LUX_APP_DATA")))
            Environment.SetEnvironmentVariable("LUX_APP_DATA", Paths.AppData("default"));
        Environment.SetEnvironmentVariable("LUX_FORBID_DEFAULT_APP_DATA", "1");
        // no test downloads the depth model: a download that a test did not give its own source reads from a folder that does not exist
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Lux.DepthModel.DepthModelManifest.SourceVariable)))
            Environment.SetEnvironmentVariable(Lux.DepthModel.DepthModelManifest.SourceVariable, Paths.AppData("no-depth-model-source"));
    }
}
