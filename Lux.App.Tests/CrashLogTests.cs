using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Lux.App.Services;
using Xunit;

namespace Lux.App.Tests;

/// <summary>The problem log (<see cref="CrashLog"/>): an exception that nothing handles is recorded in a file in the app's data folder.</summary>
public sealed class CrashLogTests
{
    static string NewFolder(string name)
    {
        string dir = Path.Combine(Paths.UiScratch, "crash-log", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        return dir;
    }

    [AvaloniaFact]
    public void Exception_in_an_event_handler_is_recorded_and_the_app_continues()
    {
        string dir = NewFolder("handler");
        CrashLog.Start(dir);
        var notices = new List<string>();
        using var attached = CrashLog.AttachUi(Dispatcher.UIThread, notices.Add);
        try
        {
            var button = new Button();
            button.Click += (_, _) => throw new InvalidOperationException("the handler of the test button failed");
            bool ranAfter = false;
            // the click arrives through the dispatcher, as input does in the running app
            Dispatcher.UIThread.Post(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Dispatcher.UIThread.Post(() => ranAfter = true);
            Dispatcher.UIThread.RunJobs();   // does not throw: the exception is recorded and handled

            Assert.True(ranAfter);
            string path = Path.Combine(dir, CrashLog.FileName);
            Assert.Equal(path, CrashLog.FilePath);
            string text = File.ReadAllText(path);
            Assert.Contains("System.InvalidOperationException: the handler of the test button failed", text);
            Assert.Contains("Lux " + AppVersion.Current, text);
            Assert.Contains("from the UI thread (the app continues)", text);
            Assert.Contains(nameof(Exception_in_an_event_handler_is_recorded_and_the_app_continues), text);   // the stack names the place
            Assert.Matches(@"==== \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2} · Lux ", text);
            Assert.Equal(new[] { CrashLog.NoticeText }, notices);
            Assert.Contains(path, notices[0]);
        }
        finally { CrashLog.Start(null); }
    }

    [AvaloniaFact]
    public void Unsafe_exception_on_the_ui_thread_is_recorded_and_not_handled()
    {
        string dir = NewFolder("unsafe");
        CrashLog.Start(dir);
        var notices = new List<string>();
        using var attached = CrashLog.AttachUi(Dispatcher.UIThread, notices.Add);
        try
        {
            Dispatcher.UIThread.Post(() => throw new AccessViolationException("unsafe state of the test"));
            Assert.Throws<AccessViolationException>(() => Dispatcher.UIThread.RunJobs());
            Assert.Contains("from the UI thread (the app ends)", File.ReadAllText(Path.Combine(dir, CrashLog.FileName)));
            Assert.Empty(notices);
        }
        finally { CrashLog.Start(null); }
    }

    [Fact]
    public void Unobserved_task_exception_is_recorded()
    {
        string dir = NewFolder("task");
        CrashLog.Start(dir);
        CrashLog.InstallProcessHandlers();
        try
        {
            // a task that fails and that nobody waits for or reads
            static void Fail() => _ = Task.Run(() => throw new InvalidDataException("the background task of the test failed"));
            Fail();
            string path = Path.Combine(dir, CrashLog.FileName);
            for (int i = 0; i < 100 && !File.Exists(path); i++)
            {
                Thread.Sleep(50);
                GC.Collect(); GC.WaitForPendingFinalizers();   // the runtime reports the exception when it collects the task
            }
            string text = File.ReadAllText(path);
            Assert.Contains("the background task of the test failed", text);
            Assert.Contains("from a background task (the app continues)", text);
        }
        finally { CrashLog.Start(null); }
    }

    [Fact]
    public void Log_file_stays_small()
    {
        string dir = NewFolder("size");
        try
        {
            string path = Path.Combine(dir, CrashLog.FileName), old = Path.Combine(dir, CrashLog.OldFileName);
            var big = new InvalidOperationException(new string('x', 2 * CrashLog.MaxRecordChars));
            for (int round = 0; round < 3; round++)
            {
                CrashLog.Start(dir);   // a new start of the app: the limit per minute counts from here
                for (int i = 0; i < CrashLog.MaxRecordsPerMinute + 5; i++) CrashLog.Write("the size test", big);
            }
            Assert.True(new FileInfo(path).Length <= CrashLog.MaxBytes);
            Assert.True(File.Exists(old));
            Assert.True(new FileInfo(old).Length <= CrashLog.MaxBytes);
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            Assert.Contains("… (cut)", File.ReadAllText(path));
            // more records in one minute than the limit: the rest are not written
            string rate = NewFolder("rate");
            CrashLog.Start(rate);
            for (int i = 0; i < CrashLog.MaxRecordsPerMinute + 3; i++) CrashLog.Write("the size test", new InvalidOperationException("small"));
            Assert.Equal(CrashLog.MaxRecordsPerMinute, File.ReadAllText(Path.Combine(rate, CrashLog.FileName)).Split("InvalidOperationException: small").Length - 1);
        }
        finally { CrashLog.Start(null); }
    }
}
