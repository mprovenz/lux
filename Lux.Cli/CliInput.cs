namespace Lux.Cli;

/// <summary>Input collection for the command-line front ends.</summary>
public static class CliInput
{
    /// <summary>Expand the positional arguments into a sorted, de-duplicated list of `.lri` files; directories are
    /// scanned (top level only) and anything else is skipped with a warning.</summary>
    public static List<string> CollectLri(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var p in inputs)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.lri", SearchOption.TopDirectoryOnly));
            else if (File.Exists(p) && p.EndsWith(".lri", StringComparison.OrdinalIgnoreCase))
                files.Add(p);
            else
                Console.Error.WriteLine($"warning: skipping '{p}' (not a .lri file or directory)");
        }
        return files.Distinct().OrderBy(f => f).ToList();
    }
}
