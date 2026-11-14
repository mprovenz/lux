namespace Lux.Engine.Mtp;

// "Mtp" here is the colloquial name for camera file transfer. The backend is libgphoto2, which speaks
// PTP (how the L16 appears on Linux) and MTP (how it appears on Windows) — files are addressed by
// (folder, name), the universal model for both. Everything here only reads from the camera: listing, file
// information and file contents. Nothing in this namespace deletes, uploads, renames or changes anything on it.

/// <summary>A file object on a connected camera (folder + name identify it).</summary>
public sealed record MtpItem(string Folder, string Name, long Size, DateTimeOffset? Modified)
{
    public string FullPath => Folder.TrimEnd('/') + "/" + Name;
    public string Extension => Path.GetExtension(Name);
}

/// <summary>A storage area on the device.</summary>
public sealed record MtpStorage(string Path, string Description);

/// <summary>A camera seen on the bus but not opened yet (<see cref="IMtpBackend.Autodetect"/>): cheap to find, so it can be polled.</summary>
/// <param name="Model">The backend's model name ("USB PTP Class Camera" for the L16).</param>
/// <param name="Port">The backend's port ("usb:004,005"); it changes when the camera is plugged in again.</param>
public sealed record MtpCandidate(string Model, string Port);

/// <summary>What an open camera says about itself (PTP device information; unknown parts are empty).</summary>
/// <param name="BatteryPercent">The battery level when the camera reports one (read when the camera is opened).</param>
public sealed record MtpDeviceInfo(string Manufacturer, string Model, string Serial, string Version, int? BatteryPercent)
{
    /// <summary>"Light L16", or the model / manufacturer alone when only one is known.</summary>
    public string DisplayName
    {
        get
        {
            if (Manufacturer.Length > 0 && Model.Length > 0)
                return Model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase) ? Model : Manufacturer + " " + Model;
            return Model.Length > 0 ? Model : Manufacturer.Length > 0 ? Manufacturer : "Camera";
        }
    }

    /// <summary>The camera is a Light L16 (manufacturer "Light", model "L16").</summary>
    public bool IsLightL16 => Manufacturer.Equals("Light", StringComparison.OrdinalIgnoreCase) && Model.Contains("L16", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Why a camera operation failed, in the terms the user can act on.</summary>
public enum MtpErrorKind
{
    /// <summary>Something else holds the camera (another program, the desktop's file manager, a second connection).</summary>
    Busy = 0,
    /// <summary>The camera went away (unplugged, switched off, the cable moved).</summary>
    Disconnected = 1,
    /// <summary>The file or the camera is not there.</summary>
    NotFound = 2,
    /// <summary>Anything else; <see cref="MtpException.Code"/> and the message say what the backend reported.</summary>
    Failed = 3,
}

/// <summary>A camera operation failed (<see cref="Kind"/> says how; <see cref="Code"/> is the backend's own error number).</summary>
public sealed class MtpException : IOException
{
    public MtpErrorKind Kind { get; }
    public int Code { get; }
    public MtpException(MtpErrorKind kind, int code, string message) : base(message) { Kind = kind; Code = code; }
}

/// <summary>Which files to pull. All set filters must match (AND); nulls are ignored.</summary>
public sealed class PullFilter
{
    /// <summary>Extensions to include (case-insensitive, with dot), e.g. [".lri"]. Null/empty = any.</summary>
    public string[]? Extensions { get; init; } = new[] { ".lri" };
    /// <summary>Filename glob (e.g. "L16_004*"). Null = any.</summary>
    public string? Glob { get; init; }
    public DateTimeOffset? ModifiedSince { get; init; }
    public DateTimeOffset? ModifiedUntil { get; init; }

    /// <summary>The filters that need only the file name (extension, glob): checked before the file's information is read.</summary>
    public bool MatchesName(string name)
    {
        if (Extensions is { Length: > 0 } &&
            !Extensions.Any(e => string.Equals(e, Path.GetExtension(name), StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Glob is not null && !GlobMatch(Glob, name)) return false;
        return true;
    }

    /// <summary>Folders whose files are never captures: the L16 keeps its calibration files (also <c>.lri</c>) in <c>lightcal</c>.</summary>
    public static bool IsCalibrationFolder(string folder) =>
        folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(p => p.Equals("lightcal", StringComparison.OrdinalIgnoreCase));

    public bool Matches(MtpItem item)
    {
        if (IsCalibrationFolder(item.Folder)) return false;
        if (!MatchesName(item.Name)) return false;
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
