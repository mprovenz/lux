namespace Lux.Engine.Mtp;

// "Mtp" here is the colloquial name for camera file transfer. The backend is libgphoto2, which speaks
// PTP (how the L16 appears on Linux) and MTP (how it appears on Windows) — files are addressed by
// (folder, name), the universal model for both.

/// <summary>A file object on a connected camera (folder + name identify it).</summary>
public sealed record MtpItem(string Folder, string Name, long Size, DateTimeOffset? Modified)
{
    public string FullPath => Folder.TrimEnd('/') + "/" + Name;
    public string Extension => Path.GetExtension(Name);
}

/// <summary>A storage area on the device.</summary>
public sealed record MtpStorage(string Path, string Description);

/// <summary>Which files to pull. All set filters must match (AND); nulls are ignored.</summary>
public sealed class PullFilter
{
    /// <summary>Extensions to include (case-insensitive, with dot), e.g. [".lri"]. Null/empty = any.</summary>
    public string[]? Extensions { get; init; } = new[] { ".lri" };
    /// <summary>Filename glob (e.g. "L16_004*"). Null = any.</summary>
    public string? Glob { get; init; }
    public DateTimeOffset? ModifiedSince { get; init; }
    public DateTimeOffset? ModifiedUntil { get; init; }

    public bool Matches(MtpItem item)
    {
        if (Extensions is { Length: > 0 } &&
            !Extensions.Any(e => string.Equals(e, item.Extension, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Glob is not null && !GlobMatch(Glob, item.Name)) return false;
        if (ModifiedSince is { } s && (item.Modified is null || item.Modified < s)) return false;
        if (ModifiedUntil is { } u && (item.Modified is null || item.Modified > u)) return false;
        return true;
    }

    private static bool GlobMatch(string pattern, string name)
    {
        string rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>Result of a pull operation.</summary>
public sealed record PullResult(int Downloaded, int Skipped, int Failed, long BytesDownloaded, IReadOnlyList<string> Files);
