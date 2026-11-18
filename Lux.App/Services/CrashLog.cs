using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace Lux.App.Services;

/// <summary>
/// The problem log: a text file in the app's data folder that records every exception nothing else handled. Each record has the time,
/// the app version, where the exception came from and its text with the stack. Three sources are recorded:
/// <list type="bullet">
/// <item>the UI thread (an event handler or a posted action). The app shows a short notice and continues, unless the state is unsafe;</item>
/// <item>a task whose exception nobody read. The app continues;</item>
/// <item>any other thread. The runtime ends the app after the record is written.</item>
/// </list>
/// The file stays small: when it passes <see cref="MaxBytes"/>, it becomes <see cref="OldFileName"/> (which replaces the one before) and a
/// new file starts. A fault inside the .NET runtime itself ends the process without running any handler, so it leaves no record here.
/// </summary>
public static class CrashLog
{
    public const string FileName = "lux-problems.log", OldFileName = "lux-problems.old.log";
    /// <summary>The size at which the file is set aside and a new one starts.</summary>
    public const long MaxBytes = 256 * 1024;
    /// <summary>The longest text of one record, in characters. A longer text is cut.</summary>
    public const int MaxRecordChars = 32 * 1024;
    /// <summary>Records written per minute at most. More are counted and the count is written with the next record.</summary>
    public const int MaxRecordsPerMinute = 30;
    /// <summary>UI-thread exceptions in ten seconds after which the app no longer continues (the same failure repeats without end).</summary>
    public const int MaxUiExceptionsInTenSeconds = 20;

    static readonly object Gate = new();
    static string? _folder;
    static bool _installed;
    static readonly Queue<DateTime> Written = new(), UiTimes = new();
    static int _dropped;

    /// <summary>The log file; null until <see cref="Start"/> names the folder.</summary>
    public static string? FilePath { get { lock (Gate) return _folder is null ? null : Path.Combine(_folder, FileName); } }

    /// <summary>Sets the folder of the log file (the app's data folder). Null stops recording.</summary>
    public static void Start(string? folder)
    {
        lock (Gate)
        {
            _folder = folder is { Length: > 0 } ? Path.GetFullPath(folder) : null;
            Written.Clear(); UiTimes.Clear(); _dropped = 0;
        }
    }

    /// <summary>Records the exceptions of threads other than the UI thread and of tasks nobody read. Call once, at the start of the app.</summary>
    public static void InstallProcessHandlers()
    {
        lock (Gate) { if (_installed) return; _installed = true; }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "a thread (the app ends)" : "a thread", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("a background task (the app continues)", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Records the exceptions that leave an action of the UI thread and lets the app continue: <paramref name="notify"/> shows a
    /// short notice. The app does not continue when <see cref="IsUnsafe"/> says so or when the exceptions repeat too fast. Dispose the
    /// result to stop.</summary>
    public static IDisposable AttachUi(Dispatcher dispatcher, Action<string>? notify)
    {
        DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
        {
            try
            {
                bool go = MayContinue(e.Exception);
                Write(go ? "the UI thread (the app continues)" : "the UI thread (the app ends)", e.Exception);
                if (!go) return;
                e.Handled = true;
                try { notify?.Invoke(NoticeText); } catch (Exception) { /* the notice is a courtesy: the record is written */ }
            }
            catch (Exception) { /* a failure here must not hide the first exception */ }
        };
        dispatcher.UnhandledException += handler;
        return new Detach(() => dispatcher.UnhandledException -= handler);
    }

    /// <summary>The notice the app shows after a recorded UI-thread exception.</summary>
    public static string NoticeText => FilePath is { } p
        ? $"Lux ran into a problem and recorded it in {p}. If something looks wrong, restart Lux."
        : "Lux ran into a problem. If something looks wrong, restart Lux.";

    /// <summary>Exceptions after which the app must not continue: its memory or its code can no longer be trusted.</summary>
    public static bool IsUnsafe(Exception e) =>
        e is OutOfMemoryException or AccessViolationException or StackOverflowException or InvalidProgramException or BadImageFormatException
            or System.Runtime.InteropServices.SEHException
        || e is AggregateException a && a.InnerExceptions.Any(IsUnsafe)
        || e.InnerException is { } inner && IsUnsafe(inner);

    static bool MayContinue(Exception e)
    {
        if (IsUnsafe(e)) return false;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            UiTimes.Enqueue(now);
            while (UiTimes.Count > 0 && now - UiTimes.Peek() > TimeSpan.FromSeconds(10)) UiTimes.Dequeue();
            return UiTimes.Count <= MaxUiExceptionsInTenSeconds;
        }
    }

    /// <summary>Appends one record. It never throws: a log that cannot be written is given up silently.</summary>
    public static void Write(string source, Exception e)
    {
        try
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (_folder is null) return;
                while (Written.Count > 0 && now - Written.Peek() > TimeSpan.FromMinutes(1)) Written.Dequeue();
                if (Written.Count >= MaxRecordsPerMinute) { _dropped++; return; }
                Written.Enqueue(now);
                string text = e.ToString();
                if (text.Length > MaxRecordChars) text = text[..MaxRecordChars] + "\n… (cut)";
                var sb = new StringBuilder();
                sb.Append("==== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                  .Append(" · Lux ").Append(AppVersion.Current).Append(" · from ").Append(source).Append('\n');
                if (_dropped > 0) { sb.Append(_dropped).Append(" more problems before this one are not recorded (too many in one minute).\n"); _dropped = 0; }
                sb.Append(text).Append("\n\n");
                Directory.CreateDirectory(_folder);
                string path = Path.Combine(_folder, FileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length + sb.Length > MaxBytes) File.Move(path, Path.Combine(_folder, OldFileName), overwrite: true);
                File.AppendAllText(path, sb.ToString());
            }
        }
        catch (Exception) { /* no log is better than a second failure */ }
    }

    sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
