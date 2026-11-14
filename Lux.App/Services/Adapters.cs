using Lux.Library;

namespace Lux.App.Services;

// App-side interfaces: the Lumen scan of a scope (a view over the contract's classification) and the camera presence.

/// <summary>What a folder holds in Lumen sessions, in the three groups of the import dialog.</summary>
/// <param name="FolderLabel">The folder as shown to the user.</param>
/// <param name="DepthEdited">Sessions with depth edits (refine edge, blend area, match depth …).</param>
/// <param name="SettingsChanged">Sessions with no depth edits but changed aperture, focus, tone or crop.</param>
/// <param name="Unchanged">Opened in Lumen but unchanged.</param>
/// <param name="AlreadyImported">Of all, how many were imported before.</param>
public sealed record LumenScanResult(string FolderLabel, IReadOnlyList<string> DepthEdited, IReadOnlyList<string> SettingsChanged,
                                     IReadOnlyList<string> Unchanged, int AlreadyImported)
{
    public int Total => DepthEdited.Count + SettingsChanged.Count + Unchanged.Count;
}

/// <summary>Finds the Lumen sessions in a scope (a folder of the library, a sub-folder, or a set of captures).</summary>
public interface ILumenScanner
{
    Task<LumenScanResult> ScanAsync(CaptureQuery scope, string folderLabel, CancellationToken cancel);
    /// <summary>Queue the imports; <paramref name="newerToneStyle"/> is the dialog's tone-style option.</summary>
    IReadOnlyList<long> Import(IReadOnlyCollection<string> captureIds, bool newerToneStyle);
}

/// <summary>Camera presence for the status bar.</summary>
public interface IDeviceStatus
{
    /// <summary>"No camera connected", "Light L16 connected · 38 new".</summary>
    string StatusText { get; }
    event EventHandler? Changed;
}

/// <summary>No camera support yet.</summary>
public sealed class NoDeviceStatus : IDeviceStatus
{
    public string StatusText => "No camera connected";
    public event EventHandler? Changed { add { } remove { } }
}
