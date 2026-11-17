using Lux.DepthModel;
using Lux.Engine.Pipeline.DepthRefine;

namespace Lux.Cli;

/// <summary>`lux-light depth-model status|install|remove`: the files of the refined depth feature (the depth network and the runtime that
/// runs it). They are not part of Lux: `install` downloads them into the app's data folder, `remove` deletes them. Exit 0 on success,
/// 1 when `status` finds them not installed, 2 on an error.</summary>
public static class DepthModelCmd
{
    public const string InstallCommand = "lux-light depth-model install";

    static string Mb(long bytes) => FormattableString.Invariant($"{bytes / 1e6:0} MB");

    public static int Run(IReadOnlyList<string> args)
    {
        string action = args.Count == 1 ? args[0] : "";
        if (action is not ("status" or "install" or "remove"))
        {
            Console.Error.WriteLine("error: depth-model takes one of: status, install, remove.");
            return 2;
        }
        DepthModelManager m;
        try { m = DepthModelManager.Default(); }
        catch (InvalidOperationException e) { Console.Error.WriteLine($"error: {e.Message}"); return 2; }
        if (m.Status.State == DepthModelState.NotAvailable) { Console.Error.WriteLine("error: " + m.Status.Reason); return 2; }

        switch (action)
        {
            case "status":
                Describe(m);
                return m.Status.State == DepthModelState.Installed ? 0 : 1;
            case "remove":
                m.Remove();
                if (m.Status.State == DepthModelState.Failed) { Console.Error.WriteLine("error: " + m.Status.Reason); return 2; }
                Console.WriteLine($"Removed the refined depth files from {m.Folder}");
                return 0;
            default:
                if (m.Status.State == DepthModelState.Installed) { Describe(m); return 0; }
                Console.WriteLine($"Downloading {DepthModelManifest.ModelName} ({DepthModelManifest.ModelLicense}) and {DepthModelManifest.RuntimeName} {m.Manifest.RuntimeVersion} "
                                  + $"({DepthModelManifest.RuntimeLicense}), about {Mb(m.DownloadBytes)}, to {m.Folder}");
                long shown = -1; bool tty = !Console.IsOutputRedirected;
                m.StatusChanged += () =>
                {
                    var s = m.Status;
                    if (!tty || s.State != DepthModelState.Downloading || s.BytesTotal == 0) return;
                    long pct = s.BytesDone * 100 / s.BytesTotal;
                    if (Interlocked.Exchange(ref shown, pct) != pct) Console.Write($"\r{pct,3} %  {Mb(s.BytesDone)} of {Mb(s.BytesTotal)}   ");
                };
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; m.CancelDownload(); };
                m.DownloadAsync().GetAwaiter().GetResult();
                if (tty) Console.WriteLine();
                switch (m.Status.State)
                {
                    case DepthModelState.Installed: Describe(m); return 0;
                    case DepthModelState.Failed: Console.Error.WriteLine("error: " + m.Status.Reason); return 2;
                    default: Console.Error.WriteLine("The download was stopped. Run the command again to continue it."); return 2;
                }
        }
    }

    static void Describe(DepthModelManager m)
    {
        var s = m.Status;
        Console.WriteLine(s.State switch
        {
            DepthModelState.Installed => $"Refined depth: installed ({s.Version}, {Mb(s.SizeOnDisk)})",
            DepthModelState.Failed => "Refined depth: " + s.Reason,
            _ => $"Refined depth: not installed. `{InstallCommand}` downloads about {Mb(m.DownloadBytes)}.",
        });
        Console.WriteLine($"Folder: {m.Folder}");
    }

    /// <summary>The network for `convert --depth-refine`. Null with <paramref name="error"/> set when the files are not installed or
    /// cannot be used: the command line never downloads them by itself.</summary>
    public static IDepthNetwork? OpenForConvert(out string? error)
    {
        error = null;
        try
        {
            var m = DepthModelManager.Default();
            switch (m.Status.State)
            {
                case DepthModelState.NotAvailable: error = "--depth-refine: " + m.Status.Reason; return null;
                case DepthModelState.Installed: return OnnxDepthNetwork.Open(m);
                default: error = $"--depth-refine needs the depth model, which is not installed. Run `{InstallCommand}` first (a download of about {Mb(m.DownloadBytes)})."; return null;
            }
        }
        catch (Exception e) when (e is DepthModelException or InvalidOperationException) { error = "--depth-refine: " + e.Message; return null; }
    }
}
