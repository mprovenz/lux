namespace Lux.Cli;

/// <summary>Thread-safe console progress lines: <c>[ n/total] message</c>.</summary>
internal static class Progress
{
    private static readonly object Gate = new();

    public static void Line(int n, int total, string message, bool error = false)
    {
        int width = total.ToString().Length;
        string prefix = $"[{n.ToString().PadLeft(width)}/{total}]";
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            if (error) Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{prefix} {message}");
            if (error) Console.ForegroundColor = prev;
        }
    }
}
