// The contract between the library core and the Develop page's depth features: the depth effect (f-number, focus, focus by
// click, the focal-region shading), the "Photo + depth" view, Lumen's depth tools (refine edge, blend area, match depth with brush / lasso /
// quick select), one combined history for depth and non-depth edits, and the photo's depth versions (clean, Lux edits, imported from Lumen).
// UI-framework-free, under the same rules as Contracts.cs / EditContracts.cs:
//  * Events are raised on library threads (never the UI thread): marshal before touching UI state.
//  * Every member is thread-safe. Calls that start work return at once; slow work runs on the photo's depth worker (one per open photo,
//    one operation at a time, in call order) and reports through Activity / ActivityChanged and the returned DepthJob.
//  * Changes after the first version are additive.
//
// Exactness: the depth effect, the overlays and every depth tool are Lumen's own, ported bit for bit. A tool run through this
// contract gives, bit for bit, the depth Lumen gives for the same stroke; the picture with the depth effect is the export's own pixels.
// Coordinates are picture coordinates (the photo as shown: saved crop / rotation / flips and the display rotation); the library maps them
// to the capture's normalised coordinates the engine takes (IPhotoView.PictureToCapture).

namespace Lux.Library;

// ====================================================================================================================== depth availability

/// <summary>Whether the depth map of the open photo can be used now.</summary>
public enum DepthAvailability
{
    /// <summary>This photo has no depth (a capture without the modules the depth needs) — the depth effect and tools are not offered.</summary>
    Unavailable = 0,
    /// <summary>Not analysed yet: <see cref="IPhotoDepth.EnsureDepthAsync"/> starts the analysis (about a minute).</summary>
    NotAnalysed = 1,
    /// <summary>The analysis (or the reading of a version's depth) is running: <see cref="DepthStatus.Progress"/>.</summary>
    Analysing = 2,
    /// <summary>The depth is ready: the depth effect, focus and tools work.</summary>
    Ready = 3,
    /// <summary>The analysis failed (<see cref="DepthStatus.Error"/>).</summary>
    Failed = 4,
}

/// <summary>Where the depth map in use comes from.</summary>
public enum DepthOrigin
{
    /// <summary>Computed by Lux from the capture (the clean version and every Lux version).</summary>
    Lux = 0,
    /// <summary>Lumen's stored depth from an imported Lumen session (slightly rounded: Lumen keeps it in a compact form).</summary>
    LumenSession = 1,
}

/// <summary>A depth interval in millimetres.</summary>
public readonly record struct DepthRange(float NearMm, float FarMm);

/// <summary>The depth state of the open photo (<see cref="IPhotoDepth.Status"/>).</summary>
public sealed record DepthStatus
{
    public DepthAvailability Availability { get; init; }
    /// <summary>0–1 while <see cref="DepthAvailability.Analysing"/>; null when unknown.</summary>
    public double? Progress { get; init; }
    /// <summary>A plain-language line for the progress ("Analysing the photo: depth", "Reading the saved depth").</summary>
    public string? ProgressText { get; init; }
    /// <summary>A plain-language reason when <see cref="DepthAvailability.Failed"/> or <see cref="DepthAvailability.Unavailable"/>.</summary>
    public string? Error { get; init; }
    public DepthOrigin Origin { get; init; }
    /// <summary>The depths the focus tool accepts (Lumen's reported depth range, `ParamFloatArray 0`): a click whose depth lies outside is
    /// refused (Lumen's "Shaded regions cannot be adjusted"); also the unshaded band of <see cref="DepthOverlayMode.FocalRegion"/> with the
    /// default <see cref="IPhotoDepth.MaximumInFocusBlurPixels"/>. Null until ready.</summary>
    public DepthRange? FocusableRange { get; init; }
    /// <summary>The nearest and farthest depth of the photo (the depth map's export window), mm. Null until ready.</summary>
    public DepthRange? SceneRange { get; init; }
    /// <summary>The range the depth view maps (see <see cref="DepthViewMapping"/>). Null until ready.</summary>
    public DepthViewMapping? ViewMapping { get; init; }
}

// ====================================================================================================================== depth effect

/// <summary>The fixed facts of the depth effect for this photo (<see cref="IPhotoDepth.Effect"/>).</summary>
/// <param name="AllInFocusFNumber">The f-number at which no blur is added (Lumen's maximum, `ParamFloat 16`: 15.2 for the 28 mm modules). The
/// slider's top; f-numbers at or above it mean "depth effect off".</param>
/// <param name="LumenMinimumFNumber">The bottom of Lumen's own slider: 2.0. (Lumen's renderer reports 4.0 as `ParamFloat 14`; its editor
/// replaces any value above 2.0 by 2.0 (its `ImageEditItem`), so Lumen's slider runs f/2.0 … all in focus.)</param>
/// <param name="FullMinimumFNumber">The lowest f-number this library accepts (1.0): the same blur model extended — Lux's own range, beyond Lumen's.</param>
/// <param name="NativeFNumber">The lens' own f-number (2.0 / 2.4).</param>
public sealed record DepthEffectInfo(float AllInFocusFNumber, float LumenMinimumFNumber, float FullMinimumFNumber, float NativeFNumber)
{
    /// <summary>Lumen's label: "f/N.N" (the f-number rounded to 0.1).</summary>
    public static string Label(float fNumber) => "f/" + MathF.Round(fNumber, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The depth effect now (<see cref="IPhotoDepth.Current"/>).</summary>
/// <param name="FNumber">The f-number setting; null = all in focus (no depth effect; Lumen's slider at its top).</param>
/// <param name="FocusMm">The focus distance setting, mm; null = Lumen's default focus (<see cref="DefaultFocusMm"/>).</param>
/// <param name="DefaultFocusMm">Lumen's default focus for this photo (`ParamFloat 15`), null until the depth is ready.</param>
/// <param name="InFocus">The depth band that renders sharp at these settings (Lumen's in-focus band with the current focus, the f-number and
/// <see cref="IPhotoDepth.MaximumInFocusBlurPixels"/>) — the "in focus from … to …" read-out; null until ready.</param>
/// <param name="FocusPoint">Where the focus was last picked (picture coordinates; Lumen's white pin); null when set by number or default.</param>
public sealed record DepthEffectState(float? FNumber, float? FocusMm, float? DefaultFocusMm, DepthRange? InFocus, NormPoint? FocusPoint)
{
    /// <summary>The depth effect is rendered (an f-number below the all-in-focus one).</summary>
    public bool IsOn { get; init; }
    /// <summary>The focus distance in use: <see cref="FocusMm"/> or the default.</summary>
    public float? EffectiveFocusMm => FocusMm ?? DefaultFocusMm;
}

/// <summary>The answer to a focus click (<see cref="IPhotoDepth.FocusAtAsync"/>).</summary>
/// <param name="Accepted">The focus was set (Lumen accepts a click only inside <see cref="DepthStatus.FocusableRange"/>).</param>
/// <param name="DepthMm">The depth at the point (Lumen's 3×3 mean), null outside the picture or when there is no depth.</param>
/// <param name="Reason">Plain language when refused ("This area cannot be brought into focus").</param>
public sealed record FocusPickResult(bool Accepted, float? DepthMm, string? Reason);

/// <summary>An overlay drawn into the photo's tiles (<see cref="ViewMode.Edited"/> only), blended in floating point before the 8-bit store
/// exactly as Lumen's renderer does.</summary>
public enum DepthOverlayMode
{
    None = 0,
    /// <summary>Lumen's "show focal region" (rendering mode 1): every pixel whose depth lies outside the focusable band (Lumen's range with
    /// <see cref="IPhotoDepth.MaximumInFocusBlurPixels"/>) is shaded toward <see cref="IPhotoDepth.FocalRegionColour"/>.</summary>
    FocalRegion = 1,
    /// <summary>The quick-select selection (rendering mode 4) — set automatically while the quick-select applicator is active.</summary>
    Selection = 4,
}

/// <summary>
/// The "Photo + depth" view's mapping (Lux's own visualisation — Lumen has no depth view): grey level = 255 · t^(1/2.2) with
/// t = (1/d − 1/far) / (1/near − 1/far) clamped to 0–1 — near is light, far is dark, linear in inverse depth (how the eye and the depth effect
/// see distance). <see cref="NearMm"/> / <see cref="FarMm"/> = the 1st and 99th percentile of the version's unedited depth inside the export
/// window, fixed while the photo is open in that version (an edit never rescales the whole view). Alpha is 255.
/// </summary>
public sealed record DepthViewMapping(float NearMm, float FarMm)
{
    public string Description => "Lux's depth view: near is light, far is dark (inverse depth between the photo's near and far range).";
}

// ====================================================================================================================== IPhotoDepth

/// <summary>
/// The depth of the open photo: availability, the depth effect (f-number and focus — part of the photo's edits: saved with them, carried by
/// copy / paste and presets, undone with the combined history), focus by click, the focal-region overlay and the depth at a point.
/// The "Photo + depth" view's tiles come from the photo's own <see cref="IPhotoView"/> with <see cref="ViewMode.Depth"/>: same geometry, same
/// tile grid, same transforms and display rotation as the photo.
/// </summary>
public interface IPhotoDepth
{
    /// <summary>The depth now. It follows what the capture keeps: when any job (a quality change, an export, an analysis started elsewhere)
    /// keeps a depth map while the photo is open, a photo without depth reads it and becomes ready. A depth the photo holds stays until the
    /// photo closes, also when the kept copy is released.</summary>
    DepthStatus Status { get; }
    event EventHandler<DepthStatus>? StatusChanged;
    /// <summary>Make the depth available: nothing when ready; else the analysis runs (a library job that keeps the result in the photo's cache)
    /// — Status follows. Completes when ready (throws <see cref="LibraryException"/> when it failed).</summary>
    Task EnsureDepthAsync(CancellationToken cancel = default);

    // ---- the depth effect (Lumen's DEPTH panel slider)
    DepthEffectInfo Effect { get; }
    DepthEffectState Current { get; }
    /// <summary>Set the f-number (one history step "Depth effect"; saved). Null or ≥ <see cref="DepthEffectInfo.AllInFocusFNumber"/> = off.
    /// Values below <see cref="DepthEffectInfo.FullMinimumFNumber"/> are raised to it; the UI chooses Lumen's range or the full one.</summary>
    void SetFNumber(float? fNumber);
    /// <summary>A slider drag: <see cref="DragFNumber"/> renders the visible picture at the drag level (coarser while the frame budget is
    /// exceeded, through the depth-effect tile cache) without a history step; <see cref="EndFNumberDrag"/> commits one step and refines.</summary>
    void BeginFNumberDrag();
    void DragFNumber(float? fNumber);
    void EndFNumberDrag();
    /// <summary>Set the focus distance by number (mm, &gt; 0; one history step "Focus"); null = Lumen's default focus.</summary>
    void SetFocus(float? focusMm);
    /// <summary>A focus slider drag, like the f-number's: <see cref="DragFocus"/> renders at the drag level without a history step,
    /// <see cref="EndFocusDrag"/> commits one "Focus" step and refines. Defaults (fakes): no drag state, every move sets the focus.</summary>
    void BeginFocusDrag() { }
    void DragFocus(float focusMm) => SetFocus(focusMm);
    void EndFocusDrag() { }
    /// <summary>Lumen's focus tool: the depth at a picture point (3×3 mean) becomes the focus — only when it lies inside
    /// <see cref="DepthStatus.FocusableRange"/> (else refused, nothing changes). With <paramref name="commit"/> false nothing is changed
    /// (hover: the UI shows the pointing hand or "forbidden").</summary>
    Task<FocusPickResult> FocusAtAsync(NormPoint picture, bool commit = true, CancellationToken cancel = default);
    /// <summary>Lumen's depth reset: all in focus and the default focus (one history step). Depth edits are untouched.</summary>
    void ResetDepthEffect();

    // ---- overlays (photo tiles of ViewMode.Edited)
    /// <summary>The overlay drawn into the photo; setting it re-renders the visible tiles (no history step, not saved).</summary>
    DepthOverlayMode Overlay { get; set; }
    /// <summary>`ParamFloat 19` (0.1–10, default 1.0): the blur, in pixels, still counted as in focus by the focal-region band and the in-focus read-out.</summary>
    float MaximumInFocusBlurPixels { get; set; }
    /// <summary>The focal-region shade, RGBA 0–1 (`ParamFloatArray 5`; Lumen's GUI: {0.2, 0.2, 0.2, 0.91} = #333333 at 91 %).</summary>
    IReadOnlyList<float> FocalRegionColour { get; set; }
    /// <summary>The selection colour of <see cref="DepthOverlayMode.Selection"/> and of the pending-stroke preview, RGBA 0–1 (`ParamFloatArray 4`;
    /// Lumen's tutorial shows a pink mask: default {1.0, 0.25, 0.55, 0.45}).</summary>
    IReadOnlyList<float> SelectionColour { get; set; }

    // ---- read-outs
    /// <summary>The depth (mm) at a picture point of the depth in use (edits included): Lumen's 3×3 mean. Null outside the picture or before
    /// the depth is ready. Cheap (no rendering): fine for a status-bar read-out under the pointer.</summary>
    float? DepthAt(NormPoint picture);

    /// <summary>The depth effect, the depth or an overlay changed. <see cref="DepthChangedEventArgs.PhotoRect"/> / <see cref="DepthChangedEventArgs.DepthRect"/>
    /// say which part of the picture to refresh (the view re-renders only those tiles itself).</summary>
    event EventHandler<DepthChangedEventArgs>? Changed;
}

/// <summary>What changed (<see cref="IPhotoDepth.Changed"/>, <see cref="IDepthEdit.Changed"/>).</summary>
public enum DepthChangeKind
{
    /// <summary>The f-number or focus (the photo changes wherever the depth effect reaches: usually everywhere).</summary>
    DepthEffect = 0,
    /// <summary>A depth edit, undo, redo or reset.</summary>
    DepthEdit = 1,
    /// <summary>The overlay or its settings, the quick-select selection, a pending stroke.</summary>
    Overlay = 2,
    /// <summary>The active version changed (everything is new).</summary>
    Version = 3,
    /// <summary>The depth became available (analysis finished).</summary>
    DepthReady = 4,
}

/// <summary>A change of the depth or of what is drawn from it.</summary>
/// <summary>A changed region of the picture at one view level (−1 = every level).</summary>
public readonly record struct PictureRegion(int Level, NormRect Rect);

public sealed class DepthChangedEventArgs : EventArgs
{
    public required DepthChangeKind Kind { get; init; }
    /// <summary>The part of the photo (<see cref="ViewMode.Edited"/> / <see cref="ViewMode.Original"/>) whose pixels may have changed, normalised
    /// picture coordinates; null = none; <see cref="NormRect.Full"/> = everything. With the depth effect on an edit can reach far beyond the
    /// stroke (Lumen's blur margins and noise follow the depth range around each tile); the rect is computed exactly from the depth-effect tiles
    /// that changed.</summary>
    public NormRect? PhotoRect { get; init; }
    /// <summary>The regions <see cref="PhotoRect"/> bounds, per view level (a level's tiles read their own depth-effect tiles; a region
    /// with <see cref="PictureRegion.Level"/> −1 applies to every level). Null = the whole of <see cref="PhotoRect"/> at every level. A held
    /// photo tile that meets no region of its level is still valid at <see cref="PhotoVersion"/>.</summary>
    public IReadOnlyList<PictureRegion>? PhotoRegions { get; init; }
    /// <summary>The part of the depth view (<see cref="ViewMode.Depth"/>) that changed; null = none.</summary>
    public NormRect? DepthRect { get; init; }
    /// <summary>The new content versions of the modes (<see cref="IPhotoView.Version"/>): tiles outside the rects stay valid at these versions.</summary>
    public long PhotoVersion { get; init; }
    public long DepthVersion { get; init; }
}

// ====================================================================================================================== depth tools

/// <summary>Lumen's depth tools (DEPTH panel).</summary>
public enum DepthTool
{
    /// <summary>"Refine edge" (Lumen `healEdge`, E): a freehand stroke along an edge whose depth looks wrong; the edge is re-cut along the
    /// photo's colours (closed-form matting). Committed on release; needs a clear near / far edge under the stroke (else nothing changes and
    /// the job says why).</summary>
    RefineEdge = 0,
    /// <summary>"Blend area" (`healSurface`, B): a freehand stroke over a surface (road, wall, table) whose depth should run smoothly; the depth
    /// under the stroke is filled in from around it. Committed on release.</summary>
    BlendArea = 1,
    /// <summary>"Match depth" (`match`, M): select an area (brush, lasso or quick select), choose the depth (picked from the photo or typed),
    /// <see cref="IDepthEdit.Apply"/> (Enter) sets the area to it.</summary>
    MatchDepth = 2,
}

/// <summary>How "match depth" selects its area.</summary>
public enum MatchApplicator
{
    /// <summary>One freehand brush stroke (Lumen's "brush"; `pushBrushDepthEdit`, hardness 0.9). <see cref="DepthToolOptions.Adaptive"/> makes
    /// it the adaptive brush (weighted by colour likeness to the first point: Lumen's engine has it, Lumen's interface never offered it).</summary>
    Brush = 0,
    /// <summary>A polygon, by clicked vertices or freehand (`pushLassoDepthEdit`), with feathering.</summary>
    Lasso = 1,
    /// <summary>Quick select: add / remove strokes grow a selection along colour edges (`addQuickSelectStrokes`); the selection is shown as an
    /// overlay after every stroke.</summary>
    QuickSelect = 2,
}

/// <summary>The options of the active tool.</summary>
/// <param name="BrushSize">Lumen's brush size (the "size:" slider and the mouse wheel): 1–100 (lasso feathering: 0–100). The tool radius is
/// <c>BrushSize × 0.001f</c> of the capture's width (as in Lumen); the lasso feather is
/// <c>BrushSize × 0.001f × 10.0f</c> (0–1). See <see cref="IDepthEdit.RadiusOf"/>, <see cref="IDepthEdit.BrushRadiusPixels"/>.</param>
/// <param name="Adaptive">Match-depth brush only: the adaptive brush (Lux offers it; Lumen's interface does not).</param>
/// <param name="AddToSelection">Quick select: add (true, Lumen's "+") or remove ("−") with the next stroke.</param>
public sealed record DepthToolOptions(float BrushSize = DepthToolOptions.DefaultBrushSize, bool Adaptive = false, bool AddToSelection = true)
{
    /// <summary>Lux's starting size (Lumen's item starts at 0 and its slider at its minimum; 20 = 2 % of the width is a usable start).</summary>
    public const float DefaultBrushSize = 20f;
    public const float MinBrushSize = 1f, MaxBrushSize = 100f, MinFeathering = 0f;
    /// <summary>An exact engine radius (fraction of the capture's width, 0 &lt; r &lt; 1) instead of <see cref="BrushSize"/> — for scripted
    /// sessions and tests; null = from the size.</summary>
    public float? RadiusOverride { get; init; }
    /// <summary>An exact lasso feather (0–1) instead of the size; null = from the size.</summary>
    public float? FeatherOverride { get; init; }
}

/// <summary>The region a quick-select stroke works on (Lumen: the level on screen and the visible part of the picture).</summary>
/// <param name="Level">Picture level 0 … 4 (the colour image the selection grows on: coarser is faster, finer follows edges better).</param>
/// <param name="Visible">The visible part of the picture (normalised picture coordinates; may reach outside 0–1); it becomes the bounding pixel
/// rectangle of that part of the capture at <paramref name="Level"/> (floor / ceiling — a value within 1e-6 of a whole pixel is that pixel —,
/// clamped to the level). A new region starts a new selection base (Lumen reloads its colour guide).</param>
public sealed record QuickSelectScope(int Level, NormRect Visible);

/// <summary>A stroke in progress (<see cref="IDepthEdit.BeginStroke"/>): points are added while the pointer moves and committed on release.</summary>
public interface IDepthStroke : IDisposable
{
    /// <summary>The tool the stroke belongs to.</summary>
    DepthTool Tool { get; }
    /// <summary>Add points (normalised picture coordinates; points may lie outside the picture, as Lumen allows). The pending-stroke preview
    /// (the area the stroke will change) follows when <see cref="IDepthEdit.ShowStrokePreview"/> is on.</summary>
    void Add(params NormPoint[] points);
    IReadOnlyList<NormPoint> Points { get; }
    /// <summary>Release: refine edge / blend area run the edit; a match-depth brush stroke or lasso becomes the pending selection (then
    /// <see cref="IDepthEdit.Apply"/>); a quick-select stroke grows / shrinks the selection. A stroke with fewer than two points does nothing.</summary>
    DepthJob End();
    /// <summary>Drop the stroke (nothing happens). Disposing an unfinished stroke cancels it.</summary>
    void Cancel();
}

/// <summary>What a depth job did.</summary>
public enum DepthJobOutcome
{
    /// <summary>Done; the depth changed (or, for a selection step, the selection did).</summary>
    Done = 0,
    /// <summary>Ran, changed nothing (<see cref="DepthJobResult.Message"/> says why — e.g. "no clear edge under the stroke").</summary>
    NothingChanged = 1,
    /// <summary>Cancelled before it changed anything (nothing is recorded).</summary>
    Cancelled = 2,
    /// <summary>Refused or failed (<see cref="DepthJobResult.Message"/>); nothing changed.</summary>
    Failed = 3,
}

/// <summary>The result of a depth job.</summary>
public sealed record DepthJobResult(DepthJobOutcome Outcome, string? Message)
{
    /// <summary>Milliseconds the work took on the worker (queue wait excluded).</summary>
    public double WorkMs { get; init; }
    /// <summary>Normalised picture rect of the depth that changed (null when nothing did).</summary>
    public NormRect? ChangedRect { get; init; }
}

/// <summary>A queued or running depth operation. Await <see cref="Completion"/> (never throws for a refused tool: see the outcome).</summary>
public sealed class DepthJob
{
    public required long Id { get; init; }
    /// <summary>"Refine edge", "Undo", "Switch to version …".</summary>
    public required string Label { get; init; }
    public required Task<DepthJobResult> Completion { get; init; }
    /// <summary>Whether it can still be stopped: queued jobs always; running ones while <see cref="DepthActivity.CanCancel"/>.</summary>
    public required Action Cancel { get; init; }
    /// <summary>A job that did nothing at once (e.g. a one-point stroke).</summary>
    public static DepthJob Completed(DepthJobResult r, string label = "") => new() { Id = 0, Label = label, Completion = Task.FromResult(r), Cancel = () => { } };
}

/// <summary>What the depth worker is doing (<see cref="IDepthEdit.Activity"/>).</summary>
/// <param name="Busy">A job is running.</param>
/// <param name="Label">The running job ("Refine edge"), null when idle.</param>
/// <param name="Phase">Plain language ("Following the edge", "Rebuilding history 3 of 12").</param>
/// <param name="Fraction">0–1 when known; null = indeterminate (show a spinner).</param>
/// <param name="ElapsedMs">Since the job started.</param>
/// <param name="EstimatedMs">The expected total from measured costs (null when unknown) — the UI can show a bar from it.</param>
/// <param name="CanCancel">The running job can be stopped now (refine edge and the adaptive brush until they write; queued jobs).</param>
/// <param name="Queued">Jobs waiting behind it.</param>
public sealed record DepthActivity(bool Busy, string? Label, string? Phase, double? Fraction, double ElapsedMs, double? EstimatedMs, bool CanCancel, int Queued)
{
    public static DepthActivity Idle { get; } = new(false, null, null, null, 0, null, false, 0);
}

/// <summary>The pending match-depth selection and target (<see cref="IDepthEdit.Pending"/>).</summary>
/// <param name="Applicator">How the area was selected.</param>
/// <param name="HasArea">An area exists (brush ≥ 2 points, lasso ≥ 3 vertices, quick select: a non-empty selection) — Lumen's `hasClosedEditArea`.</param>
/// <param name="TargetDepthMm">The depth the area will get: typed, or the depth at <paramref name="ReferencePoint"/> (read at apply time, as Lumen
/// does). Null until chosen.</param>
/// <param name="ReferencePoint">The picked point (picture coordinates), null when typed or not chosen.</param>
/// <param name="LassoPoints">The lasso's vertices so far (picture coordinates).</param>
/// <param name="SelectedPixels">Quick select: pixels in the selection (at the edited level, 4160 × 3120).</param>
public sealed record PendingSelection(MatchApplicator Applicator, bool HasArea, float? TargetDepthMm, NormPoint? ReferencePoint, IReadOnlyList<NormPoint> LassoPoints,
                                      long SelectedPixels)
{
    /// <summary>Apply is possible (an area and a target depth).</summary>
    public bool CanApply => HasArea && (TargetDepthMm is not null || ReferencePoint is not null);
}

// ====================================================================================================================== history (combined)

/// <summary>What a history step changed.</summary>
public enum HistoryEntryKind
{
    /// <summary>Tone and colour.</summary>
    Adjustment = 0,
    /// <summary>Crop, rotation, flips.</summary>
    Geometry = 1,
    /// <summary>The f-number or focus.</summary>
    DepthEffect = 2,
    /// <summary>A paste, preset, reset or "revert to original" of several groups.</summary>
    Several = 3,
    /// <summary>A depth edit (refine edge, blend area, match depth).</summary>
    DepthEdit = 4,
    /// <summary>The depth edits imported from Lumen (the base of a Lumen version: not undoable).</summary>
    DepthImport = 5,
}

/// <summary>One step of the combined history.</summary>
/// <param name="Index">Position in the list (0 = oldest).</param>
/// <param name="Label">"Exposure", "Rotate right", "Refine edge", "Match depth (lasso)", …</param>
/// <param name="Done">Applied (false = undone, available to redo).</param>
/// <param name="Kind">What it changed.</param>
/// <param name="Persistent">Kept with the photo (depth edits, in the photo's edit database): it is still listed after the photo is closed and
/// opened again. Non-depth steps live for this session only (their result is saved, the steps are not).</param>
/// <param name="Undoable">It can be undone: false for depth edits beyond Lumen's 20-edit limit (still saved, just not undoable), for an import
/// base, and for the saved steps of an unreliable history. While the history is being rebuilt the steps stay undoable: an undo waits for the
/// rebuild (started at once).</param>
public sealed record HistoryEntry(int Index, string Label, bool Done, HistoryEntryKind Kind, bool Persistent, bool Undoable)
{
    /// <summary>The depth tool of a depth edit.</summary>
    public DepthTool? Tool { get; init; }
    public MatchApplicator? Applicator { get; init; }
    /// <summary>A depth step saved by an earlier session of the photo (read back from its log at open), not made in this one.</summary>
    public bool FromEarlierSession { get; init; }
}

/// <summary>Whether the depth history can be used.</summary>
public enum DepthHistoryState
{
    /// <summary>Complete: every listed depth step can be undone / redone (within the 20-edit limit).</summary>
    Ready = 0,
    /// <summary>The photo was opened from its saved result; the steps are being rebuilt in the background (<see cref="HistoryInfo.Progress"/>).
    /// The picture is final; undo of a depth step waits for the rebuild (it is started at once when needed).</summary>
    Rebuilding = 1,
    /// <summary>The saved steps do not reproduce the saved result (a damaged entry, another Lux version): the picture is kept exactly as saved,
    /// but earlier depth steps cannot be undone (<see cref="HistoryInfo.Reason"/>). Recovery: <see cref="IDepthEdit.StartCleanVersion"/> or keep
    /// editing on top.</summary>
    Unreliable = 2,
}

/// <summary>The combined history (<see cref="IPhotoHistory.Info"/>).</summary>
/// <param name="Entries">Done steps oldest first, then the undone ones in redo order.</param>
/// <param name="Position">Steps applied (= index after the last done step).</param>
/// <param name="State">The depth history's state.</param>
/// <param name="Progress">0–1 while rebuilding.</param>
/// <param name="Reason">Plain language when unreliable.</param>
/// <param name="DepthLimit">Lumen's in-session limit of undoable depth edits (20).</param>
/// <param name="DepthOperationsSaved">Entries in the active version's saved log (every operation incl. undo / redo / reset and selection
/// strokes: the store keeps all of them, unbounded).</param>
public sealed record HistoryInfo(IReadOnlyList<HistoryEntry> Entries, int Position, DepthHistoryState State, double? Progress, string? Reason, int DepthLimit, long DepthOperationsSaved)
{
    /// <summary>While <see cref="DepthHistoryState.Rebuilding"/>: the saved operations replayed so far and how many there are
    /// ("Rebuilding earlier steps · 3 of 5"); null when no rebuild is running.</summary>
    public int? RebuiltSteps { get; init; }
    /// <summary>Counts the steps the list shows from the earlier session (not the saved operations, which include
    /// selection strokes, undo / redo and resets); <see cref="RebuiltSteps"/> follows in proportion to the operations replayed.</summary>
    public int? StepsToRebuild { get; init; }
    /// <summary>The saved operations replayed so far and how many there are.</summary>
    public int? OperationsReplayed { get; init; }
    public int? OperationsToReplay { get; init; }
    public static HistoryInfo Empty { get; } = new(Array.Empty<HistoryEntry>(), 0, DepthHistoryState.Ready, null, null, 20, 0);
}

/// <summary>
/// One history for all of the photo's edits, in the order they were made (the history panel and Ctrl+Z / Shift+Ctrl+Z):
/// <list type="bullet">
/// <item>Tone / colour / geometry / depth-effect steps (<see cref="IPhotoEdit"/>) and depth edits (<see cref="IDepthEdit"/>) form one list.</item>
/// <item><see cref="Undo"/> undoes the newest done step whatever its kind; <see cref="Redo"/> redoes the oldest undone one. A new step of any kind
/// discards every undone step (of both kinds) from the list.</item>
/// <item>Depth steps are persistent (the photo's edit log); the others are per session. After closing and opening the photo again the list holds
/// the depth steps only (rebuilt from the saved log in the background: <see cref="DepthHistoryState.Rebuilding"/>); tone and geometry are as saved,
/// with no steps to undo.</item>
/// <item>Lumen's limit applies to depth steps: 20 undoable at a time (older ones stay done and saved: <see cref="HistoryEntry.Undoable"/> false).</item>
/// <item>Switching the depth version starts the list again (that version's depth steps); the tone / geometry steps of the session are dropped.</item>
/// <item><see cref="IPhotoEdit.Undo"/> / <see cref="IDepthEdit.Undo"/> (and their redo) still act on their own kind only (Lumen's separate menu
/// items); the combined list shows the result, each entry with its own done flag.</item>
/// </list>
/// Every step changes the picture at once (the views follow); depth steps run on the depth worker and report through <see cref="IDepthEdit.Activity"/>.
/// </summary>
public interface IPhotoHistory
{
    HistoryInfo Info { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    /// <summary>"Undo exposure", "Redo refine edge".</summary>
    string? UndoLabel { get; }
    string? RedoLabel { get; }
    /// <summary>Undo the newest done step (a depth step goes through the depth worker: await the job for its end).</summary>
    DepthJob Undo();
    DepthJob Redo();
    /// <summary>Undo / redo until <paramref name="position"/> steps are applied (the panel's jump).</summary>
    DepthJob GoTo(int position);
    event EventHandler<HistoryInfo>? Changed;
    /// <summary>"Clear history": the picture stays as it is and the listed steps go — this session's tone, crop / rotation
    /// and depth-effect steps, and the depth edits listed so far, which can then no longer be undone in this session (the version's saved
    /// record of depth edits is kept, as Lumen's: they are listed again as earlier steps the next time the photo is opened).</summary>
    DepthJob ClearHistory() => DepthJob.Completed(new DepthJobResult(DepthJobOutcome.NothingChanged, "This photo's history cannot be cleared."));
    /// <summary>The history was cleared in this session: the list starts at that moment.</summary>
    bool Cleared => false;
}

// ====================================================================================================================== versions

/// <summary>What a depth version is.</summary>
public enum DepthVersionKind
{
    /// <summary>The photo's own depth as Lux computes it, without depth edits (always there; never stored).</summary>
    Clean = 0,
    /// <summary>Depth edits made in Lux, with their full history.</summary>
    Lux = 1,
    /// <summary>Imported from a Lumen session: Lumen's stored depth and edits (lower fidelity — see <see cref="IDepthEdit.LumenImportWarning"/>).</summary>
    LumenImport = 2,
}

/// <summary>One depth version of the photo.</summary>
/// <param name="Id">"clean", or the stored variant's name ("lux", "lux-2", "lumen-import").</param>
/// <param name="Name">Plain language ("Original depth", "Lux edits", "Lux edits 2", "From Lumen").</param>
/// <param name="EditCount">Depth edits applied in it now (the undo cursor).</param>
/// <param name="OperationCount">Entries in its saved log.</param>
/// <param name="HistoryUnreliable">Its saved history is marked unreliable.</param>
/// <remarks><see cref="Id"/> is stable ("clean" for the clean version, else the stored variant's name, e.g. "lux", "lux-2",
/// "lumen-import"); <see cref="Name"/> is only the library's suggestion ("Clean", "Lux edits", "Lux edits 2", "From Lumen") — the app names
/// versions as it likes from <see cref="Kind"/> and <see cref="Id"/>.</remarks>
public sealed record DepthVersion(string Id, string Name, DepthVersionKind Kind, bool Active, int EditCount, long OperationCount, DateTime? CreatedUtc, bool HistoryUnreliable)
{
    /// <summary>Lumen's newer tone style was chosen when it was imported (Lumen versions only).</summary>
    public bool NewerToneStyle { get; init; }
}

// ====================================================================================================================== IDepthEdit

/// <summary>
/// Lumen's depth tools on the open photo, its depth history and its depth versions. Every edit is applied to the photo's depth at once
/// (the picture with the depth effect, the depth view and the overlays follow, refreshing only what changed), recorded in the photo's edit
/// database (beside the photo in <c>LuxImageContext</c>, or in the local store) and mirrored in the library (depth-edited flag, version,
/// operation count, thumbnail). Tools need the depth (<see cref="IPhotoDepth.Status"/> Ready).
/// </summary>
public interface IDepthEdit
{
    // ---- the tool
    /// <summary>The active tool, null = none (Lumen: one of focus / refine edge / blend area / match depth / crop at a time — the UI keeps the
    /// exclusivity; activating a tool here cancels a pending selection of another).</summary>
    DepthTool? Tool { get; }
    MatchApplicator Applicator { get; }
    DepthToolOptions Options { get; }
    /// <summary>Choose the tool (and the match-depth applicator). The quick-select applicator turns <see cref="DepthOverlayMode.Selection"/> on;
    /// leaving it restores the previous overlay.</summary>
    void SetTool(DepthTool? tool, MatchApplicator applicator = MatchApplicator.Lasso);
    void SetOptions(DepthToolOptions options);
    /// <summary>The engine radius of a brush size (<c>size × 0.001f</c>, fraction of the capture's width), as Lumen's editor computes it.</summary>
    float RadiusOf(float brushSize);
    /// <summary>The lasso feather of a size (<c>size × 0.001f × 10.0f</c>).</summary>
    float FeatherOf(float brushSize);
    /// <summary>The brush circle's radius in full-size picture pixels (level 0) — multiply by the screen pixels per picture pixel for the cursor.
    /// The circle is round in the picture (transforms are quarter turns and crops).</summary>
    double BrushRadiusPixels(float brushSize);
    /// <summary>Draw the area a stroke in progress will change (the brush / heal mask at its real size; the lasso polygon) into the photo tiles,
    /// mode 4 semantics with <see cref="IPhotoDepth.SelectionColour"/>. Default on.</summary>
    bool ShowStrokePreview { get; set; }

    // ---- strokes and selections
    /// <summary>Start a stroke of the active tool (refine edge / blend area / match-depth brush / quick select; a lasso drawn freehand). For quick
    /// select <paramref name="scope"/> = the colour level and visible part (null = from the viewer's last request: its target level, but not
    /// finer than needed to keep the region under about 1.2 megapixels — Lumen uses the level on screen, which is level 4 at fit).</summary>
    IDepthStroke BeginStroke(QuickSelectScope? scope = null);
    /// <summary>Lasso by clicks: add a vertex (picture coordinates). Three or more close the area.</summary>
    void AddLassoPoint(NormPoint picture);
    /// <summary>Set the target depth by number (mm, &gt; 100) or pick it from the photo (Lumen's eyedropper: the depth at the point, read when
    /// applying).</summary>
    void SetTargetDepth(float depthMm);
    void PickTargetDepth(NormPoint picture);
    /// <summary>The pending match-depth selection, null when there is none.</summary>
    PendingSelection? Pending { get; }
    /// <summary>The quick-select selection as bytes (0 / 1) at the edited level (<see cref="SelectionSize"/>, row-major), null when there is none.</summary>
    byte[]? SelectionMask();
    PixelSize SelectionSize { get; }
    /// <summary>Apply the pending selection with its target depth (Lumen's Enter): one history step.</summary>
    DepthJob Apply();
    /// <summary>Drop the pending selection (Lumen's reset of the tool: the strokes, the lasso, the quick-select selection). No history step.</summary>
    void CancelPending();

    // ---- worker
    DepthActivity Activity { get; }
    event EventHandler<DepthActivity>? ActivityChanged;
    /// <summary>Stop the running job when it can be stopped and drop the queued ones.</summary>
    void CancelAll();

    // ---- depth history (Lumen's undo / redo / reset depth edits; the combined list is IPhotoSession.History)
    bool CanUndo { get; }
    bool CanRedo { get; }
    DepthJob Undo();
    DepthJob Redo();
    /// <summary>Lumen's "reset depth edits": the version's depth back to its base (one recorded operation; not undoable, as in Lumen).</summary>
    DepthJob Reset();
    DepthHistoryState HistoryState { get; }

    // ---- versions (non-destructive: nothing is ever deleted here)
    IReadOnlyList<DepthVersion> Versions { get; }
    DepthVersion ActiveVersion { get; }
    /// <summary>Render and edit another version from now on (it becomes the photo's active version for exports too).</summary>
    DepthJob SwitchVersion(string versionId);
    /// <summary>A new Lux version from the photo's clean depth (made active); the others stay.</summary>
    DepthJob StartCleanVersion();
    /// <summary>The Lumen session beside the photo, when there is one (its path); null otherwise.</summary>
    string? LumenSessionPath { get; }
    /// <summary>The text to show before importing (plain language).</summary>
    string LumenImportWarning { get; }
    /// <summary>Import the Lumen session beside the photo as a version (made active), with Lumen's newer tone style or not (a session does not
    /// record which one Lumen used). Importing the same session again only updates the tone-style choice.</summary>
    DepthJob ImportLumenSession(bool newerToneStyle);

    event EventHandler<DepthChangedEventArgs>? Changed;
}

// ====================================================================================================================== defaults for fakes

/// <summary>The depth part of a session that has none (fakes, viewers without depth): everything unavailable, every command a no-op.</summary>
public sealed class NoPhotoDepth : IPhotoDepth, IDepthEdit, IPhotoHistory
{
    public static NoPhotoDepth Instance { get; } = new();
    static readonly DepthJob None = DepthJob.Completed(new DepthJobResult(DepthJobOutcome.Failed, "This photo has no depth."));
    public DepthStatus Status { get; } = new() { Availability = DepthAvailability.Unavailable, Error = "This photo has no depth." };
#pragma warning disable CS0067
    public event EventHandler<DepthStatus>? StatusChanged;
    public event EventHandler<DepthChangedEventArgs>? Changed;
    public event EventHandler<DepthActivity>? ActivityChanged;
    event EventHandler<HistoryInfo>? IPhotoHistory.Changed { add { } remove { } }
#pragma warning restore CS0067
    public Task EnsureDepthAsync(CancellationToken cancel = default) => Task.FromException(new LibraryException("This photo has no depth."));
    public DepthEffectInfo Effect { get; } = new(15.217391f, 2f, 1f, 2f);
    public DepthEffectState Current { get; } = new(null, null, null, null, null);
    public void SetFNumber(float? fNumber) { }
    public void BeginFNumberDrag() { }
    public void DragFNumber(float? fNumber) { }
    public void EndFNumberDrag() { }
    public void SetFocus(float? focusMm) { }
    public Task<FocusPickResult> FocusAtAsync(NormPoint picture, bool commit = true, CancellationToken cancel = default) => Task.FromResult(new FocusPickResult(false, null, "This photo has no depth."));
    public void ResetDepthEffect() { }
    public DepthOverlayMode Overlay { get => DepthOverlayMode.None; set { } }
    public float MaximumInFocusBlurPixels { get => 1f; set { } }
    public IReadOnlyList<float> FocalRegionColour { get => new[] { 0.2f, 0.2f, 0.2f, 0.91f }; set { } }
    public IReadOnlyList<float> SelectionColour { get => new[] { 1f, 0.25f, 0.55f, 0.45f }; set { } }
    public float? DepthAt(NormPoint picture) => null;
    public DepthTool? Tool => null;
    public MatchApplicator Applicator => MatchApplicator.Lasso;
    public DepthToolOptions Options { get; } = new();
    public void SetTool(DepthTool? tool, MatchApplicator applicator = MatchApplicator.Lasso) { }
    public void SetOptions(DepthToolOptions options) { }
    public float RadiusOf(float brushSize) => brushSize * 0.001f;
    public float FeatherOf(float brushSize) => brushSize * 0.001f * 10.0f;
    public double BrushRadiusPixels(float brushSize) => 0;
    public bool ShowStrokePreview { get => false; set { } }
    public IDepthStroke BeginStroke(QuickSelectScope? scope = null) => throw new InvalidOperationException("This photo has no depth.");
    public void AddLassoPoint(NormPoint picture) { }
    public void SetTargetDepth(float depthMm) { }
    public void PickTargetDepth(NormPoint picture) { }
    public PendingSelection? Pending => null;
    public byte[]? SelectionMask() => null;
    public PixelSize SelectionSize => default;
    public DepthJob Apply() => None;
    public void CancelPending() { }
    public DepthActivity Activity => DepthActivity.Idle;
    public void CancelAll() { }
    public bool CanUndo => false;
    public bool CanRedo => false;
    public DepthJob Undo() => None;
    public DepthJob Redo() => None;
    public DepthJob Reset() => None;
    public DepthHistoryState HistoryState => DepthHistoryState.Ready;
    public IReadOnlyList<DepthVersion> Versions { get; } = new[] { new DepthVersion("clean", "Original depth", DepthVersionKind.Clean, true, 0, 0, null, false) };
    public DepthVersion ActiveVersion => Versions[0];
    public DepthJob SwitchVersion(string versionId) => None;
    public DepthJob StartCleanVersion() => None;
    public string? LumenSessionPath => null;
    public string LumenImportWarning => "";
    public DepthJob ImportLumenSession(bool newerToneStyle) => None;
    HistoryInfo IPhotoHistory.Info => HistoryInfo.Empty;
    bool IPhotoHistory.CanUndo => false;
    bool IPhotoHistory.CanRedo => false;
    string? IPhotoHistory.UndoLabel => null;
    string? IPhotoHistory.RedoLabel => null;
    DepthJob IPhotoHistory.Undo() => None;
    DepthJob IPhotoHistory.Redo() => None;
    DepthJob IPhotoHistory.GoTo(int position) => None;
}
