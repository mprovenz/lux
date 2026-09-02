namespace Lux.Cli;

/// <summary>The `lux-light` command table, kept in one place so that the dispatch, `--help` and the "unknown command"
/// path cannot drift apart. Adding a command means adding it here and to the dispatch.
/// <para>Public, together with <see cref="Options"/> and <see cref="CliInput"/>: a front end built on this assembly
/// reads the table as an ordinary library reference instead of copying it.</para></summary>
public static class CommandSets
{
    public const string ProductionExe = "lux-light";

    /// <summary>The 8 commands. `convert` is the one picture-producing verb; every output is one of its `--formats`.</summary>
    public static readonly string[] Production =
    {
        "convert", "inspect", "profile", "isp", "isp-run", "mod-info", "devices", "pull",
    };

    public static bool IsProduction(string command) => Array.IndexOf(Production, command) >= 0;

    /// <summary>`error: unknown command 'x'.` on stderr.</summary>
    public static void ReportUnknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'.");
    }
}
