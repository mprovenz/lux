using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// The two causes of the intermittent app-test failures, reproduced on purpose (env-gated; run this class alone; log in the workspace's output folder).
/// <para><b>Font manager disposed under queued work</b> (LUX_TEARDOWN_REPRO=1, tests A then B). Under Avalonia.Headless's default per-test
/// isolation the session tears a test down in this order: the FontManager is disposed (its font collections cleared), then
/// <c>Dispatcher.ResetForUnitTests</c> RUNS every job still queued, then the locator scope goes. A job a background thread posted in that
/// window (the app's library events, thumbnails and job updates do) that shapes text meets the disposed manager: a character the primary face
/// lacks falls through to <c>FontManager.SystemFonts</c> = <c>_fontCollections["fonts:SystemFonts"]</c> → KeyNotFoundException, failing
/// the ending test. With <c>AvaloniaTestIsolationLevel.PerAssembly</c> (TestSetup.cs) the manager is never disposed and B passes.</para>
/// <para><b>A frame finalised while it is written</b> (LUX_FRAME_SAVE_REPRO=old or fixed, test C). Avalonia.Skia's
/// <c>WriteableBitmapImpl.Save</c> encodes <c>SKImage.FromPixels(bitmap pixels)</c>, an image that only borrows the pixels. With nothing
/// referencing the frame during the encode (a capture without <c>using</c>: <c>var frame = top.CaptureRenderedFrame(); frame.Save(path);</c>),
/// a collection finalises the SKBitmap, its pixel memory is unmapped, and <c>sk_image_encode</c> reads freed memory: SIGSEGV, "Test host
/// process crashed" (SEGV_MAPERR in sk_image_encode ← managed Save). "old" crashes the host within a few saves while
/// another thread collects; "fixed" (<c>using var frame</c>, as Harness.Capture does) saves them all.</para>
/// </summary>
public sealed class HeadlessTeardownRepro
{
    static bool On => Environment.GetEnvironmentVariable("LUX_TEARDOWN_REPRO") == "1";
    internal static Exception? Seen;
    internal static bool AEnded, JobRan;
    static void Log(string m) => File.AppendAllText(Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "crash", "repro.log"), $"{DateTime.Now:HH:mm:ss.fff} {m}\n");

    [AvaloniaFact]
    public void A_test_ends_with_a_text_job_queued()
    {
        if (!On) return;
        Log("A: a background thread keeps posting status text (as the app's library events and job updates do) while the test ends");
        AEnded = true;
        var field = typeof(FontManager).GetField("_fontCollections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = field.GetValue(FontManager.Current)!;
        var count = dict.GetType().GetProperty("Count")!;
        var t = new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool disposedSeen = false;
            while (sw.ElapsedMilliseconds < 3000)
            {
                bool disposed = (int)count.GetValue(dict)! == 0;
                if (disposed && !disposedSeen) { disposedSeen = true; Log("background thread: the test's FontManager has been disposed (its collections are empty)"); }
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        JobRan = true;
                        try
                        {
                            var ft = new FormattedText("Rating ★★★ · 3 of 5", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                                       new Typeface(new FontFamily("avares://lux-app/Assets/Fonts#Archivo")), 12, Brushes.White);
                            _ = ft.Width;
                        }
                        catch (Exception e) { if (Seen is null) { Seen = e; Log($"job failed: {e.GetType().Name}: {e.Message}"); } throw; }
                    }, DispatcherPriority.Background);
                }
                catch (Exception e) { Log($"post failed: {e.GetType().Name}"); break; }
                if (disposedSeen && sw.ElapsedMilliseconds > 200) break;
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        t.Start();
        Thread.Sleep(50);
    }

    [AvaloniaFact]
    public void B_the_next_test_runs()
    {
        if (!On) return;
        // under per-test isolation the jobs above ran during A's teardown, after the font manager was disposed
        Log($"B: A ended {AEnded}, its job ran {JobRan}, failed {Seen?.GetType().Name ?? "no"}");
        Assert.True(Seen is null, $"a text job queued at the end of the previous test failed during its teardown: {Seen?.GetType().Name}: {Seen?.Message}");
    }

    [AvaloniaFact]
    public void C_frames_are_saved_while_another_thread_collects()
    {
        var mode = Environment.GetEnvironmentVariable("LUX_FRAME_SAVE_REPRO");
        if (mode is not ("old" or "fixed")) return;
        var win = new Window { Width = 1440, Height = 900, Background = Brushes.DarkSlateGray, Content = new TextBlock { Text = "frame save", FontSize = 40 } };
        win.Show();
        var folder = Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "crash", "frames");
        Directory.CreateDirectory(folder);
        bool stop = false;
        var gc = new Thread(() => { while (!Volatile.Read(ref stop)) { GC.Collect(); GC.WaitForPendingFinalizers(); } }) { IsBackground = true };
        gc.Start();
        Log($"C ({mode}): 40 saves of a 1440×900 frame while another thread collects");
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var path = Path.Combine(folder, $"{mode}-{i % 4}.png");
                if (mode == "old") SaveUnreferenced(win, path);
                else Harness.Capture(win, $"{mode}-{i % 4}", folder);
            }
        }
        finally { Volatile.Write(ref stop, true); gc.Join(); win.Close(); }
        Log($"C ({mode}): all 40 saves done");
    }

    /// <summary>A capture without <c>using</c>: nothing references the frame once its Save has started.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void SaveUnreferenced(TopLevel top, string path)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        frame.Save(path);
    }
}
