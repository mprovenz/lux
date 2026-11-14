using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lux.App.Fake;
using Lux.App.Viewer;
using Lux.App.ViewModels;
using Lux.App.Views;

namespace Lux.App.Tests;

/// <summary>Helpers for the Develop page on the fake library and the fake editor.</summary>
public static class DevelopHarness
{
    public static DevelopView DevelopView(this Harness h) => h.Window.FindControl<DevelopView>("DevelopPage")!;
    public static PhotoViewer Viewer(this Harness h) => h.DevelopView().PhotoViewer;
    public static DevelopViewModel Develop(this Harness h) => h.Vm.Develop;
    public static FakeDevelopService FakeDevelop(this Harness h) => (FakeDevelopService)h.Services.Develop;
    public static FakeDevelopSession Session(this Harness h) => (FakeDevelopSession)h.Develop().Session!;

    /// <summary>The screenshot scope with L16_00493 open in Develop, its picture fully on screen.</summary>
    public static async Task<Harness> OpenAsync(FakeDevelopOptions? develop = null, string name = "L16_00493", double width = 1440, double height = 900)
    {
        var h = await Harness.CreateAsync(develop: develop, width: width, height: height);
        await h.MockupSelectionAsync();
        var item = h.Library.Items.First(i => i.Name == name);
        h.Library.Click(item, false, false);
        h.Vm.OpenInDevelop(item);
        await h.SettlePictureAsync();
        return h;
    }

    /// <summary>Let the viewer ask, the fake render and the arrivals land, until the target level of the current version is on screen.</summary>
    public static async Task SettlePictureAsync(this Harness h, Func<bool>? until = null, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await h.SettleAsync(timeoutMs: 200, waitThumbnails: false);
            var v = h.Viewer();
            v.CompleteAnimation();
            Dispatcher.UIThread.RunJobs();
            h.Window.RenderFrame();
            bool ok = h.Develop().Session != null && v.LevelOnScreen >= 0 && v.LevelOnScreen <= v.TargetLevel && !v.IsHolding
                      && h.Develop().Session!.Save.Status != Lux.App.Develop.SaveStatus.Saving && (until?.Invoke() ?? true);
            quiet = ok ? quiet + 1 : 0;
            if (quiet >= 2) break;
            await Task.Delay(20);
        }
    }
}
