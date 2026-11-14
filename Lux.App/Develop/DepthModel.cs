using System.Globalization;
using Lux.App.Viewer;
using Lux.Library;

namespace Lux.App.Develop;

// App-side model of the Develop page's depth work: what the depth view models bind to. The fake (Fake/FakeDepth.cs) and the
// adapter over the library's depth contract (ContractDepth.cs over Lux.Library/DepthContracts.cs) implement it. Coordinates are always the
// picture as shown (normalised 0–1, after the saved turn, flip, crop and the display-only held turn): strokes, focus clicks and picks are in
// the picture the user sees, the library maps them to the capture.

/// <summary>Whether the open photo's depth can be used now.</summary>
public enum DepthReadiness
{
    /// <summary>The depth map is there: the depth effect, focus and every depth tool work.</summary>
    Ready,
    /// <summary>Never analysed: nothing depth-related works until an analysis has run ("Analyse now").</summary>
    NotAnalysed,
    /// <summary>The analysis is running or waiting in the Queue.</summary>
    Analysing,
    /// <summary>The last analysis failed (<see cref="DepthStatusInfo.Message"/> says why).</summary>
    Failed,
    /// <summary>The library cannot edit depth (an older library, a read-only session).</summary>
    Unavailable,
}

/// <param name="Progress">0–1 while analysing; NaN when unknown or waiting.</param>
/// <param name="Message">A plain line for the failed / unavailable states.</param>
public sealed record DepthStatusInfo(DepthReadiness Readiness, double Progress = double.NaN, string? Message = null)
{
    public bool IsReady => Readiness == DepthReadiness.Ready;
    public static readonly DepthStatusInfo Ready = new(DepthReadiness.Ready);
}

/// <summary>The ranges of the depth effect for this photo.</summary>
/// <param name="WidestFNumber">The strongest effect the slider reaches (f/2.0 on the L16's wide modules).</param>
/// <param name="AllInFocusFNumber">The f-number at which no blur is added (Lumen's default f-number, f/15.2 at 28 mm): the "Off" end.</param>
/// <param name="NearestMm">The focus slider's near end (the nearest depth in the photo, mm).</param>
/// <param name="FarthestMm">The focus slider's far end (mm).</param>
/// <param name="DefaultFocusMm">Lumen's default focus distance (the camera's focus point).</param>
public sealed record DepthEffectRange(double WidestFNumber, double AllInFocusFNumber, double NearestMm, double FarthestMm, double DefaultFocusMm)
{
    public static readonly DepthEffectRange Placeholder = new(2.0, 15.2, 500, 100_000, 4000);
}

/// <summary>The outcome of a focus click.</summary>
/// <param name="DepthMm">The depth at the point (focus is set there when <paramref name="Ok"/>).</param>
/// <param name="Reason">Why the point cannot be used, in plain language.</param>
public sealed record PointResult(bool Ok, double? DepthMm, string? Reason = null);

/// <summary>Lumen's depth tools (the focus tool and crop are the page's own).</summary>
public enum DepthToolKind
{
    /// <summary>Refine edge (E): a stroke along an edge; on release the depth there is re-cut along the photo's edge.</summary>
    RefineEdge,
    /// <summary>Blend area (B): on release the depth under the stroke is filled in smoothly from around it.</summary>
    BlendArea,
    /// <summary>Match depth (M): select an area (brush, lasso or quick select), choose the depth, Apply.</summary>
    MatchDepth,
}

/// <summary>How match depth selects its area.</summary>
public enum MatchMode { Brush, Lasso, QuickSelect }

/// <summary>The options of the active tool.</summary>
/// <param name="BrushSize">Lumen's size, 1–100 (the lasso's feathering: 0–100); the brush radius is size × 0.001 of the capture's width.</param>
/// <param name="FollowColour">Match depth brush: paint only where the colour is like the start of the stroke (Lumen's adaptive brush).</param>
/// <param name="AddToSelection">Quick select: the next stroke adds (true) or removes.</param>
public sealed record ToolOptions(double BrushSize = 20, bool FollowColour = false, bool AddToSelection = true)
{
    public const double MinSize = 1, MaxSize = 100, DefaultSize = 20;
}

/// <summary>The result of a depth job (a tool, an apply, an undo, a version switch).</summary>
public enum ToolResult
{
    /// <summary>Done: the depth (or the selection) changed.</summary>
    Applied,
    /// <summary>Ran but changed nothing (<see cref="ToolOutcome.Message"/> says why); no history step.</summary>
    NothingChanged,
    /// <summary>Refused or failed; nothing changed.</summary>
    Failed,
    /// <summary>Cancelled by the user; nothing changed.</summary>
    Cancelled,
}

public sealed record ToolOutcome(ToolResult Result, string? Message = null)
{
    public static readonly ToolOutcome Applied = new(ToolResult.Applied);
    public static readonly ToolOutcome Cancelled = new(ToolResult.Cancelled);
    public bool Changed => Result == ToolResult.Applied;
}

/// <summary>
/// One freehand stroke of the active tool: points are sent as the pointer moves (never blocking the pointer); <see cref="EndAsync"/> runs it
/// (refine edge, blend area), makes it the pending selection (match depth brush, freehand lasso) or grows / shrinks the selection (quick select).
/// Points are in the picture as shown, normalised.
/// </summary>
public interface IToolStroke
{
    void Add(IReadOnlyList<NormPoint> points);
    Task<ToolOutcome> EndAsync();
    void Cancel();
}

/// <summary>What the depth worker is doing now.</summary>
/// <param name="Running">A job is running.</param>
/// <param name="Label">"Refining the edge", "Rebuilding history 3 of 12".</param>
/// <param name="Progress">0–1, NaN when unknown (a spinner).</param>
/// <param name="CanCancel">The running job can be stopped.</param>
/// <param name="EstimatedMs">The expected total, when known.</param>
/// <param name="Queued">Jobs waiting behind it (a quick-select stroke drawn while the previous one computes waits its turn).</param>
public sealed record ToolActivity(bool Running, string Label = "", double Progress = double.NaN, bool CanCancel = false, double? EstimatedMs = null, int Queued = 0)
{
    public static readonly ToolActivity Idle = new(false);
}

/// <summary>Match depth's pending selection: nothing in the depth map changes until it is applied.</summary>
/// <param name="HasArea">An area exists (brush ≥ 2 points, lasso ≥ 3 corners, quick select: something selected).</param>
/// <param name="TargetMm">The typed target depth, null when picked or not chosen.</param>
/// <param name="ReferencePoint">The picked point (picture as shown), null when typed or not chosen.</param>
/// <param name="LassoPoints">The lasso's corners so far.</param>
/// <param name="SelectedFraction">Quick select: the selected share of the picture (0–1), NaN when unknown.</param>
public sealed record PendingArea(MatchMode Mode, bool HasArea, double? TargetMm, NormPoint? ReferencePoint, IReadOnlyList<NormPoint> LassoPoints,
                                      double SelectedFraction = double.NaN)
{
    public bool HasTarget => TargetMm != null || ReferencePoint != null;
    public bool CanApply => HasArea && HasTarget;
}

/// <summary>The depth history's state (the saved depth steps are replayed in the background so they can be undone).</summary>
public enum HistoryState
{
    /// <summary>Every listed step can be revisited.</summary>
    Ready,
    /// <summary>Saved steps are being rebuilt: the picture is final, but they cannot be undone for a moment.</summary>
    Rebuilding,
    /// <summary>The saved steps do not reproduce the saved picture: the picture is kept exactly, earlier depth steps cannot be undone.</summary>
    Unreliable,
}

/// <param name="Done">Steps rebuilt so far (when known; else see <paramref name="Progress"/>).</param>
/// <param name="Total">Steps to rebuild (0 when unknown).</param>
/// <param name="Progress">0–1 while rebuilding, NaN when unknown.</param>
/// <param name="Message">Plain language for the unreliable state.</param>
public sealed record HistoryStatus(HistoryState State, int Done = 0, int Total = 0, double Progress = double.NaN, string? Message = null)
{
    public static readonly HistoryStatus Ready = new(HistoryState.Ready);
}

/// <summary>What kind of step a history row is (depth rows are marked in the panel).</summary>
public enum HistoryKind
{
    Opened,
    /// <summary>Tone and colour.</summary>
    Tone,
    /// <summary>Crop, rotation, flips.</summary>
    Geometry,
    /// <summary>Aperture or focus.</summary>
    DepthEffect,
    /// <summary>A depth tool (refine edge, blend area, match depth), a depth reset.</summary>
    DepthEdit,
    /// <summary>A depth version switch or import.</summary>
    Version,
    /// <summary>Several kinds at once (paste, preset, revert).</summary>
    Mixed,
}

/// <summary>One row of the combined history ("Opened" first, then every step oldest first; done steps, then the undone ones).</summary>
/// <param name="Saved">A depth step kept with the photo from an earlier session.</param>
/// <param name="Undoable">The step can be undone now (false while the saved steps are rebuilt, when the history is unreliable, beyond Lumen's 20
/// depth edits, before a depth reset).</param>
public sealed record HistoryItem(string Label, HistoryKind Kind, bool Saved = false, bool Undoable = true)
{
    public bool IsDepth => Kind is HistoryKind.DepthEffect or HistoryKind.DepthEdit or HistoryKind.Version;
}

public enum VersionKind
{
    /// <summary>Lux's own depth, no edits.</summary>
    Clean,
    /// <summary>Depth edits made in Lux.</summary>
    Lux,
    /// <summary>Depth edits imported from a Lumen session.</summary>
    Lumen,
}

/// <param name="Id">The library's variant id ("clean", "lux", "lux-2", "lumen-import").</param>
/// <param name="Name">"Lux edits", "Clean", "From Lumen".</param>
/// <param name="Steps">Depth edits applied in it now.</param>
/// <param name="Note">A short line for the tooltip.</param>
public sealed record DepthVersionInfo(string Id, VersionKind Kind, string Name, int Steps, bool IsCurrent, string? Note = null, bool HistoryUnreliable = false);

/// <summary>
/// The depth of one open photo — the app-side mirror of the library's depth contract (<c>IPhotoDepth</c> + <c>IDepthEdit</c>): the depth effect
/// and focus, the depth view and the overlays, Lumen's depth tools, and the depth parts of history and versions (the combined history list is the
/// session's <see cref="IDevelopSession.HistoryItems"/>). Members are called on the UI thread and never wait on a render or a tool; slow work
/// reports through <see cref="Activity"/> and the returned tasks. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public interface IDepthSession
{
    // ---- readiness
    DepthStatusInfo Status { get; }
    /// <summary>Start the analysis now (the library's job; the status follows).</summary>
    void AnalyseNow();

    // ---- the depth effect
    DepthEffectRange EffectRange { get; }
    /// <summary>The depths the focus tool accepts (Lumen's range); also the unshaded band of the focal-region shade. Null until ready.</summary>
    (double NearMm, double FarMm)? FocusableRange { get; }
    /// <summary>The f-number of the effect; null = off (all in focus).</summary>
    double? FNumber { get; }
    double FocusMm { get; }
    bool FocusIsDefault { get; }
    /// <summary>Where focus was last set by a click (picture as shown); null for the default focus or a typed value.</summary>
    NormPoint? FocusPoint { get; }
    /// <summary>The depth band that renders sharp now (mm; far = +∞ when open); null when the effect is off or not ready.</summary>
    (double NearMm, double FarMm)? InFocus { get; }
    /// <summary>A slider drag: the aperture previews live at a coarser level; a focus value is committed at the end (no history step until then).</summary>
    void BeginEffectDrag();
    void DragEffect(double? fNumber, double focusMm);
    void EndEffectDrag();
    /// <summary>Committed values (typed, double-click, a key step): one history step per value that changed.</summary>
    void SetEffect(double? fNumber, double focusMm);
    /// <summary>Click to focus (picture point as shown): the depth there becomes the focus (one step), or the reason it cannot.</summary>
    Task<PointResult> FocusAtAsync(NormPoint picture, CancellationToken cancel = default);
    /// <summary>Whether a focus click at the point would be accepted (from the depth in memory; the focus tool's cursor).</summary>
    bool CanFocusAt(NormPoint picture);
    /// <summary>Back to the default focus (one step).</summary>
    void ResetFocus();
    /// <summary>Lumen's depth reset: the effect off and the default focus (one step); depth edits are untouched.</summary>
    void ResetEffect();

    // ---- views and overlays
    /// <summary>Shade the parts that cannot be brought into focus (Lumen's "show focal region").</summary>
    bool ShadeOutsideFocus { get; set; }
    /// <summary>The shade as a tile layer over the picture (Mask layer); null when the library draws it into the picture's own tiles.</summary>
    ITileSource? ShadeLayer { get; }
    /// <summary>The depth map as a picture (near is light), same tile grid and geometry as the photo.</summary>
    ITileSource DepthView { get; }
    /// <summary>The depth at a picture point from what is in memory (instant); null when unknown (sky, not analysed, outside).</summary>
    double? DepthAt(NormPoint picture);
    /// <summary>The near and far ends of the depth view's grey ramp (mm), for the legend.</summary>
    (double NearMm, double FarMm) DepthViewRange { get; }

    // ---- tools
    DepthToolKind? Tool { get; }
    MatchMode Mode { get; }
    ToolOptions Options { get; }
    /// <summary>Choose the tool (null = none) and match depth's mode; quick select shows its selection over the picture.</summary>
    void SetTool(DepthToolKind? tool, MatchMode mode = MatchMode.Brush);
    void SetOptions(ToolOptions options);
    /// <summary>The brush's diameter in picture pixels at full size for a size (the cursor circle; the size read-out).</summary>
    double BrushDiameterPx(double size);
    /// <summary>Start a freehand stroke of the active tool (throws <see cref="InvalidOperationException"/> when the depth is not ready).</summary>
    IToolStroke BeginStroke();
    /// <summary>Match depth's lasso by clicks: one corner (three or more make the area).</summary>
    void AddLassoPoint(NormPoint picture);
    /// <summary>The depth to set, typed (mm).</summary>
    void SetTarget(double depthMm);
    /// <summary>The depth to set, picked from the photo (read when applying, as Lumen does).</summary>
    void PickTarget(NormPoint picture);
    PendingArea? Pending { get; }
    /// <summary>The pending selection as a mask layer, drawn tinted on the Mask layer; null when the library draws it into the picture.</summary>
    ITileSource? MaskLayer { get; }
    /// <summary>How strongly the selection is tinted (0.1–1).</summary>
    double SelectionOpacity { get; set; }
    /// <summary>Apply the pending selection with its target (Enter): one history step.</summary>
    Task<ToolOutcome> ApplyPendingAsync();
    /// <summary>Drop the pending selection (the strokes, the lasso, the quick-select selection; no history step).</summary>
    void CancelPending();
    ToolActivity Activity { get; }
    void CancelActivity();

    // ---- history and versions
    HistoryStatus HistoryStatus { get; }
    bool HasDepthEdits { get; }
    /// <summary>Lumen's "reset depth edits": the version's depth back to its start (recorded; cannot be undone, as in Lumen).</summary>
    Task<ToolOutcome> ResetDepthEditsAsync();
    IReadOnlyList<DepthVersionInfo> Versions { get; }
    /// <summary>Show another version (nothing is lost: each version keeps its own edits; the history restarts with that version's steps).</summary>
    Task<ToolOutcome> SwitchVersionAsync(string id);
    /// <summary>A new Lux version from the clean depth, made current (the others are kept).</summary>
    Task<ToolOutcome> StartCleanVersionAsync();
    /// <summary>The capture has a Lumen session whose edits can be imported here.</summary>
    bool CanImportLumen { get; }
    /// <summary>The fidelity warning to show before importing.</summary>
    string LumenImportWarning { get; }
    /// <summary>Import the photo's Lumen session as a version (made current).</summary>
    Task<ToolOutcome> ImportLumenAsync(bool newerToneStyle);

    event Action? Changed;
}

/// <summary>Distances and f-numbers in the depth panel's words.</summary>
public static class DepthText
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>"0.82 m", "4.56 m", "45.6 m", "∞".</summary>
    public static string Distance(double mm)
    {
        if (double.IsInfinity(mm) || mm >= 1e8) return "∞";
        double m = mm / 1000;
        return m < 10 ? m.ToString("0.00", En) + " m" : m < 100 ? m.ToString("0.0", En) + " m" : Math.Round(m).ToString("0", En) + " m";
    }

    /// <summary>"f/2.0", "f/5.6"; null → "Off".</summary>
    public static string FNumber(double? f) => f is double v ? "f/" + v.ToString("0.0", En) : "Off";

    /// <summary>Read a typed distance: "4.56", "4.56 m", "456 cm", "4560 mm" (metres when no unit). False when unreadable.</summary>
    public static bool TryParseDistance(string? text, out double mm)
    {
        mm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.').ToLowerInvariant();
        double scale = 1000;
        if (t.EndsWith("mm", StringComparison.Ordinal)) { scale = 1; t = t[..^2]; }
        else if (t.EndsWith("cm", StringComparison.Ordinal)) { scale = 10; t = t[..^2]; }
        else if (t.EndsWith('m')) t = t[..^1];
        if (!double.TryParse(t.Trim(), NumberStyles.Float, En, out var v) || double.IsNaN(v) || v <= 0 || double.IsInfinity(v)) return false;
        mm = v * scale;
        return true;
    }

    /// <summary>Read a typed f-number: "2", "f/2", "f2.8", "5.6"; "off" / "∞" → null (off). False when unreadable.</summary>
    public static bool TryParseFNumber(string? text, out double? f)
    {
        f = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.').ToLowerInvariant();
        if (t is "off" or "∞" or "none" or "all in focus") return true;
        if (t.StartsWith("f/", StringComparison.Ordinal)) t = t[2..];
        else if (t.StartsWith('f')) t = t[1..];
        if (!double.TryParse(t.Trim(), NumberStyles.Float, En, out var v) || double.IsNaN(v) || v <= 0 || double.IsInfinity(v)) return false;
        f = v;
        return true;
    }
}
