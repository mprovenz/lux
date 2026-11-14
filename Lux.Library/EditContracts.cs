// The contract between the library core and the app's Develop page: viewing a photo through a viewport-driven picture
// source, its non-depth edits (tone and colour, crop, rotation, flips), undo / redo, automatic saving, the histogram, copy / paste and
// presets. UI-framework-free, like Contracts.cs, and under the same rules:
//  * Events are raised on library threads (never the UI thread): marshal before touching UI state.
//  * Every member is thread-safe. Calls that change an edit return at once; the picture follows through TileReady.
//  * Changes after the first version are additive where possible.
//
// Exactness: what the viewer shows is the export's own pixels. A tile of the edited picture at level L is, byte for byte, the
// corresponding region of the JPG the export of level L would encode (before the JPEG coding) with the same saved settings; an export of
// the saved state equals `lux-light convert` with the same settings. Rotations, flips and crops are Lumen's own transform operations.

using Lux.Engine.Pipeline;

namespace Lux.Library;

// ====================================================================================================================== opening

/// <summary>Pixel layout of the tiles a <see cref="IPhotoView"/> delivers (4 bytes per pixel, row-major, no padding).</summary>
public enum PixelOrder
{
    /// <summary>R, G, B, A (the engine's own order).</summary>
    Rgba = 0,
    /// <summary>B, G, R, A (Avalonia's <c>PixelFormat.Bgra8888</c>).</summary>
    Bgra = 1,
}

/// <summary>How a photo is opened (<see cref="ILibrary.OpenPhotoAsync"/>).</summary>
public sealed record PhotoOpenOptions
{
    /// <summary>The byte order of every tile and stand-in picture.</summary>
    public PixelOrder PixelOrder { get; init; } = PixelOrder.Rgba;
    /// <summary>The picture tile edge in level pixels (64–2048; tiles at the right and bottom edges are smaller).</summary>
    public int TileSize { get; init; } = 512;
    /// <summary>Open for viewing only: the edit members throw <see cref="InvalidOperationException"/> and nothing is ever saved.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// One photo open in the Develop page: its picture source and its edit state, sharing one engine session (render state, tile caches,
/// depth). Dispose it when the photo is closed (an unsaved change is saved first; disposing never throws for a failed save — see
/// <see cref="IPhotoEdit.SaveState"/>). Opening a second session of the same photo is allowed but each keeps its own caches.
/// </summary>
public interface IPhotoSession : IDisposable, IAsyncDisposable
{
    string CaptureId { get; }
    /// <summary>The library item at open (re-fetch with <see cref="ILibrary.Get"/> after <see cref="ILibrary.CapturesChanged"/>).</summary>
    CaptureItem Item { get; }
    IPhotoView View { get; }
    IPhotoEdit Edit { get; }
    /// <summary>The depth, the depth effect and focus (DepthContracts.cs). The default is <see cref="NoPhotoDepth"/>.</summary>
    IPhotoDepth Depth => NoPhotoDepth.Instance;
    /// <summary>Lumen's depth tools, the depth history and the depth versions.</summary>
    IDepthEdit DepthEdit => NoPhotoDepth.Instance;
    /// <summary>One history for depth and non-depth edits (the history panel, Ctrl+Z).</summary>
    IPhotoHistory History => NoPhotoDepth.Instance;
}

// ====================================================================================================================== geometry types

/// <summary>A rectangle in normalised picture coordinates: (0, 0) = top left of the picture as shown, (1, 1) = bottom right.</summary>
public readonly record struct NormRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public static NormRect Full { get; } = new(0, 0, 1, 1);
}

/// <summary>A point in normalised coordinates (0–1 across and down).</summary>
public readonly record struct NormPoint(double X, double Y);

/// <summary>A rectangle in whole pixels of one level (X, Y = top left; exclusive right / bottom = X + Width, Y + Height).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>A tile of the picture: level and grid position (see <see cref="PictureGeometry.TileRect"/>).</summary>
public readonly record struct TileId(int Level, int Col, int Row);

/// <summary>
/// The picture as it is shown now: the photo's saved transform (crop, rotation by quarter turns, flips — Lumen's own) and the display
/// rotation (<see cref="CaptureItem.DisplayRotation"/>, "show photos as held") applied. Level 0 is full size; level L is the export of level L
/// with the same transform (its size is Lumen's export size rule: the level's export window × the crop size), so sizes are not exact halvings
/// — always use <see cref="LevelSizes"/>. Changes when the transform or the layout changes (<see cref="IPhotoView.GeometryChanged"/>).
/// </summary>
/// <param name="LevelSizes">Picture size per level 0 … 4, as shown.</param>
/// <param name="TileSize">Tile edge in level pixels.</param>
/// <param name="DisplayRotation">Degrees clockwise added for display only (0 when the photo has a saved rotation or the setting is off).</param>
/// <param name="Layout">The layout these sizes belong to.</param>
/// <param name="Version">Increases with every geometry change (tiles carry it).</param>
public sealed record PictureGeometry(IReadOnlyList<PixelSize> LevelSizes, int TileSize, int DisplayRotation, PictureLayout Layout, long Version)
{
    /// <summary>What caused this geometry.</summary>
    public GeometryChangeCause Cause { get; init; }
    /// <summary>When the new picture is the previous one turned (same framing, same mirroring): the degrees clockwise (90 / 180 / 270), so the
    /// viewer can hold the old picture turned until the new tiles come; null otherwise (a crop, a flip, a reset).</summary>
    public int? TurnFromPrevious { get; init; }
    public int LevelCount => LevelSizes.Count;
    public PixelSize FullSize => LevelSizes[0];
    /// <summary>Columns and rows of a level's tile grid.</summary>
    public (int Cols, int Rows) Grid(int level)
    {
        var s = LevelSizes[level];
        return ((s.Width + TileSize - 1) / TileSize, (s.Height + TileSize - 1) / TileSize);
    }
    /// <summary>The pixel rectangle of a tile (smaller at the right and bottom edges).</summary>
    public PixelRect TileRect(int level, int col, int row)
    {
        var s = LevelSizes[level];
        int x = col * TileSize, y = row * TileSize;
        return new PixelRect(x, y, Math.Min(TileSize, s.Width - x), Math.Min(TileSize, s.Height - y));
    }
    /// <summary>The tiles of a level covering a normalised rectangle (clamped to the picture).</summary>
    public IReadOnlyList<TileId> TilesCovering(int level, NormRect r)
    {
        var s = LevelSizes[level]; var (cols, rows) = Grid(level);
        int x0 = Math.Clamp((int)Math.Floor(r.X * s.Width) / TileSize, 0, cols - 1), x1 = Math.Clamp((int)Math.Ceiling(r.Right * s.Width - 1) / TileSize, 0, cols - 1);
        int y0 = Math.Clamp((int)Math.Floor(r.Y * s.Height) / TileSize, 0, rows - 1), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom * s.Height - 1) / TileSize, 0, rows - 1);
        var list = new List<TileId>();
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) list.Add(new TileId(level, x, y));
        return list;
    }
}

/// <summary>What caused a geometry change (<see cref="PictureGeometry.Cause"/>).</summary>
public enum GeometryChangeCause
{
    Unknown = 0,
    /// <summary>The photo was opened.</summary>
    Open = 1,
    Rotate = 2,
    FlipHorizontal = 3,
    FlipVertical = 4,
    /// <summary>A crop was applied.</summary>
    Crop = 5,
    /// <summary>The crop tool was shown or hidden (layout change).</summary>
    CropTool = 6,
    /// <summary>Geometry reset or "revert to original".</summary>
    Reset = 7,
    Undo = 8,
    Redo = 9,
    /// <summary>A paste or preset (from the editor or the library).</summary>
    Paste = 10,
    /// <summary>The "show photos as held" setting changed.</summary>
    DisplaySetting = 11,
}

/// <summary>What the picture shows.</summary>
public enum PictureLayout
{
    /// <summary>The photo as saved: cropped, turned and flipped (what an export makes).</summary>
    Picture = 0,
    /// <summary>The crop tool's layout: turned and flipped, not cropped — the frame (<see cref="IPhotoEdit.CropFrame"/>) is drawn over it.
    /// Shown while <see cref="IPhotoEdit.CropState"/> is not <see cref="CropState.Hidden"/>.</summary>
    Uncropped = 1,
}

/// <summary>Which picture a tile belongs to (before / after).</summary>
public enum ViewMode
{
    /// <summary>The photo with its current edits.</summary>
    Edited = 0,
    /// <summary>The same photo without tone and colour adjustments (Lumen's own rendering), with the same crop, rotation and depth
    /// effect — the "before" of a before / after view.</summary>
    Original = 1,
    /// <summary>The depth map as a picture ("Photo + depth"): same geometry, tiles, transforms and display rotation as the photo; Lux's own grey
    /// mapping (<see cref="DepthViewMapping"/>). Needs the depth (an unanalysed photo delivers nothing until the depth is ready).</summary>
    Depth = 2,
}

// ====================================================================================================================== the viewer

/// <summary>What the viewer wants now (<see cref="IPhotoView.RequestViewport"/>). The newest request wins: work for older requests is
/// cancelled between tiles.</summary>
public sealed record ViewportRequest
{
    /// <summary>The visible part of the picture, normalised (may reach outside 0–1; clamped).</summary>
    public required NormRect Visible { get; init; }
    /// <summary>Device pixels the visible rectangle covers on screen (its width and height). The renderer refines to the coarsest level that
    /// has at least this many picture pixels across it (level 0 at 100 % and above), capped by the quality.</summary>
    public required double ScreenWidth { get; init; }
    public required double ScreenHeight { get; init; }
    /// <summary>Force a level (instead of choosing it from the screen size); still capped by the quality.</summary>
    public int? Level { get; init; }
    /// <summary>The highest quality the viewer may show (the quality selector); null = the best the photo has now
    /// (<see cref="CaptureItem.BestQuality"/>). Asking above what is ready does not start any processing — use
    /// <see cref="ILibrary.RequestQuality"/>; the status says what is on screen.</summary>
    public ViewQuality? QualityCap { get; init; }
    /// <summary>Which picture (before / after). Each mode has its own versions.</summary>
    public ViewMode Mode { get; init; } = ViewMode.Edited;
    /// <summary>The user is panning or zooming: tiles are rendered at the target level only (the coarse level-4 picture is not redone)
    /// and the job queue is told the user is interacting (<see cref="IJobQueue.SetInteractive"/>).</summary>
    public bool Interactive { get; init; }
    /// <summary>The tiles the viewer still needs at the target level (it holds the others at the current version); null = every tile of the
    /// visible rectangle. The coarse level-4 picture always comes whole unless the viewer already holds it. The tiles are rendered
    /// in the order of this collection (give an ordered list, e.g. centre-out); null = centre-out.</summary>
    public IReadOnlyCollection<TileId>? Needed { get; init; }
    /// <summary>Whether the viewer already holds the whole level-4 picture at the current version.</summary>
    public bool HasCoarse { get; init; }
    /// <summary>Library-internal: re-render only the tiles meeting this region (a depth change, a stroke preview).</summary>
    internal NormRect? RefreshOnly { get; init; }
    /// <summary>Library-internal: with <see cref="RefreshOnly"/>, the regions themselves (a tile is re-rendered only when it meets one).</summary>
    internal IReadOnlyList<PictureRegion>? RefreshAreas { get; init; }
}

/// <summary>One delivered tile. The pixel array belongs to the receiver (it is not reused by the library).</summary>
public sealed class PictureTile
{
    public required ViewMode Mode { get; init; }
    public required TileId Id { get; init; }
    /// <summary>The tile's pixel rectangle in level <see cref="TileId.Level"/> of the picture (<see cref="PictureGeometry.TileRect"/>).</summary>
    public required PixelRect Rect { get; init; }
    /// <summary>The content version it shows (<see cref="IPhotoView.Version"/> of its mode when it was made).</summary>
    public required long Version { get; init; }
    /// <summary>The geometry version it belongs to (<see cref="PictureGeometry.Version"/>): drop tiles of an older geometry.</summary>
    public required long GeometryVersion { get; init; }
    /// <summary>The request it answers (<see cref="IPhotoView.RequestViewport"/>'s return value).</summary>
    public required long Request { get; init; }
    /// <summary><see cref="Rect"/>.Width × Height × 4 bytes in <see cref="Order"/>, stride = Width × 4.</summary>
    public required byte[] Pixels { get; init; }
    public required PixelOrder Order { get; init; }
    /// <summary>Rendered at the slider-drag level (coarser than the request); a refined tile follows when the drag ends.</summary>
    public bool IsDragPreview { get; init; }
}

/// <summary>What the viewer is doing (<see cref="IPhotoView.StatusChanged"/>).</summary>
public sealed record ViewStatus
{
    /// <summary>The quality of the finest tiles delivered for the current request.</summary>
    public ViewQuality OnScreen { get; init; }
    /// <summary>The finest level the current request may reach (0 full detail, 1 the 13 MP render, 2 the preview).</summary>
    public int FinestLevel { get; init; }
    /// <summary>The level the current request refines to.</summary>
    public int TargetLevel { get; init; }
    /// <summary>Tiles are being rendered.</summary>
    public bool Rendering { get; init; }
    /// <summary>Tiles delivered / planned for the current request.</summary>
    public int TilesDone { get; init; }
    public int TilesPlanned { get; init; }
    /// <summary>The readiness of the requested quality (<see cref="ILibrary.GetQualityState"/>) — when it is not <see cref="QualityReadiness.Ready"/>
    /// the UI shows the blurred stand-in with a spinner and "Processing image" over the best picture available.</summary>
    public QualityState? Quality { get; init; }
    /// <summary>The first picture of the photo is still being made (nothing at all on screen yet): show the stand-in.</summary>
    public bool WaitingForFirstPicture { get; init; }
    /// <summary>A plain-language line when something failed ("This photo cannot be read: …"); null otherwise.</summary>
    public string? Error { get; init; }
    /// <summary>The photo is rendered from an imported Lumen session (Lumen's own camera geometry and depth): its 13 MP and full-detail pictures
    /// are never read from the kept renders, so the viewer shows the preview unless a quality is asked for.</summary>
    public bool RendersFromLumenSession { get; init; }
    /// <summary>A plain-language line explaining the quality on screen when it is not what the kept renders suggest (the Lumen-session case);
    /// null otherwise.</summary>
    public string? QualityNote { get; init; }
}

/// <summary>The blurred stand-in for the "Processing image" placeholder: the best small picture available, blurred.</summary>
/// <param name="Pixels">Width × Height × 4 bytes in <paramref name="Order"/>, already blurred; null when only a JPEG is available.</param>
/// <param name="Jpeg">The library thumbnail (JPEG) when no rendered picture exists yet: decode it and blur it in the UI.</param>
public sealed record StandInImage(int Width, int Height, byte[]? Pixels, PixelOrder Order, byte[]? Jpeg);

/// <summary>
/// The Develop page's picture source: a tile pyramid of the edited picture, delivered progressively — the whole level-4 picture first
/// (about 520 pixels wide, on screen at once), then the visible tiles of the level matching the zoom, finest last. Every change of an edit
/// re-renders through the engine's stage cache (only the stages after the first one the change affects run again). While a slider is
/// dragged tiles come at a coarser level within a frame budget (<see cref="ViewerSettings"/>) and are refined when it is released.
/// </summary>
public interface IPhotoView
{
    string CaptureId { get; }
    /// <summary>The picture's geometry now (sizes per level as shown, tile grid).</summary>
    PictureGeometry Geometry { get; }
    /// <summary>The picture's geometry changed (crop, rotation, flip, crop tool shown or hidden, display rotation): sizes and tiles are new;
    /// drop every held tile and request again.</summary>
    event EventHandler<PictureGeometry>? GeometryChanged;

    /// <summary>Ask for the tiles of a viewport. Returns the request number (tiles carry it). Older requests are cancelled between tiles.</summary>
    long RequestViewport(ViewportRequest request);
    /// <summary>A tile is ready (library thread).</summary>
    event EventHandler<PictureTile>? TileReady;

    /// <summary>The content version of a mode now: tiles of an older version show an older state of the edits.</summary>
    long Version(ViewMode mode);
    /// <summary>The oldest version still worth showing for a mode: a committed change, an undo, a reset or a geometry change raise it; live
    /// slider moves do not (so the picture keeps moving while a drag outpaces the renders). Drop arriving tiles older than it.</summary>
    long Floor(ViewMode mode);
    /// <summary>The content version changed (an edit was made): request again (the held tiles of older versions may stay until replaced).</summary>
    event EventHandler<ViewMode>? VersionChanged;

    /// <summary>What is on screen, what is being made, and whether to show the processing placeholder.</summary>
    ViewStatus Status { get; }
    event EventHandler<ViewStatus>? StatusChanged;

    /// <summary>The stand-in for the "Processing image" placeholder (blurred level-4 picture once rendered, else the library thumbnail);
    /// null when nothing exists yet (show a neutral square).</summary>
    Task<StandInImage?> GetStandInAsync(CancellationToken cancel = default);

    /// <summary>A picture point (normalised, as shown in <see cref="PictureLayout.Picture"/>) → the capture's own coordinates: normalised over
    /// the full-size export window, unrotated and uncropped (what focus points and depth tools take). Null outside the picture.</summary>
    NormPoint? PictureToCapture(NormPoint picture);
    /// <summary>The inverse of <see cref="PictureToCapture"/>; null when the capture point is cropped away.</summary>
    NormPoint? CaptureToPicture(NormPoint capture);
    /// <summary>The scene depth (mm) at a picture point — Lumen's click to focus (3×3 mean of the depth cache). Null when the photo is not
    /// analysed yet and <paramref name="analyseIfNeeded"/> is false (an analysis takes about a minute).</summary>
    Task<float?> DepthAtAsync(NormPoint picture, bool analyseIfNeeded = false, CancellationToken cancel = default);

    /// <summary>Render a region of a level at once (tests and exports of a view): the same pixels the tiles carry. Not progressive.</summary>
    Task<byte[]> RenderRegionAsync(int level, PixelRect rect, ViewMode mode = ViewMode.Edited, CancellationToken cancel = default);
}

// ====================================================================================================================== edits

/// <summary>The tone and colour controls, in the order of Lumen's panel (the last three are Lux's: Lumen implements them but never offered them).</summary>
public enum AdjustmentKey
{
    Exposure = 0,
    Contrast = 1,
    Temperature = 2,
    Tint = 3,
    Vibrance = 4,
    Saturation = 5,
    Sharpening = 6,
    Shadows = 7,
    Highlights = 8,
    Clarity = 9,
}

/// <summary>Groups for reset, copy / paste and presets (Lumen's copy dialog: GEOMETRY {rotation &amp; mirror, crop}, TONE &amp; COLOR).</summary>
[Flags]
public enum EditGroups
{
    None = 0,
    /// <summary>Exposure, contrast, shadows, highlights, clarity.</summary>
    Tone = 1,
    /// <summary>Temperature, tint, vibrance, saturation.</summary>
    Colour = 2,
    /// <summary>Sharpening.</summary>
    Sharpening = 4,
    /// <summary>The crop rectangle (and its aspect).</summary>
    Crop = 8,
    /// <summary>Rotation and mirroring (quarter turns, flips).</summary>
    RotationAndMirror = 16,
    /// <summary>The depth effect's f-number and focus (edited in a later version; carried by copy / paste and presets already).</summary>
    DepthEffect = 32,
    /// <summary>Every tone and colour control.</summary>
    ToneAndColour = Tone | Colour | Sharpening,
    /// <summary>Crop, rotation and mirroring.</summary>
    Geometry = Crop | RotationAndMirror,
    All = ToneAndColour | Geometry | DepthEffect,
}

/// <summary>One slider: range, default, step and labels (plain language).</summary>
/// <param name="Name">"Exposure", "Temperature", …</param>
/// <param name="Unit">"EV", "K", "" …</param>
/// <param name="Default">The value of an unedited photo. For temperature and tint: this photo's own white balance ("as shot").</param>
/// <param name="Step">The slider's step (keyboard arrows); any value in range is accepted.</param>
/// <param name="Decimals">Digits after the decimal point for the read-out.</param>
/// <param name="LuxOnly">Not offered by Lumen's own editor (shadows, highlights, clarity).</param>
/// <param name="Note">"JPG and preview only" — every tone control acts on the JPG and the preview, never on DNG, HDR or PPM.</param>
public sealed record AdjustmentInfo(AdjustmentKey Key, EditGroups Group, string Name, string Unit, float Min, float Max, float Default, float Step,
                                    int Decimals, bool LuxOnly, string Note);

/// <summary>Lumen's crop aspect presets (`GeometryEdits::CropMode`, same numbers): landscape free, 4:3, 16:9, 3:2, 2:1, 1:1 and the portrait ones.</summary>
public enum CropAspect
{
    Free = 0,
    Ratio4x3 = 1,
    Ratio3x4 = 2,
    Ratio16x9 = 3,
    Ratio9x16 = 4,
    Ratio3x2 = 5,
    Ratio2x3 = 6,
    Ratio2x1 = 7,
    Ratio1x2 = 8,
    Ratio1x1 = 9,
}

/// <summary>The crop tool's state (Lumen's `GeometryEdits::CropState`).</summary>
public enum CropState
{
    /// <summary>Not cropping: the picture is shown cropped.</summary>
    Hidden = 0,
    /// <summary>The crop frame is shown over the uncropped picture.</summary>
    Shown = 1,
    /// <summary>The frame is being dragged.</summary>
    Active = 2,
}

/// <summary>
/// The photo transform as the editor describes it, and exactly as Lumen stores it. <see cref="Matrix"/> / <see cref="CropX0"/>… are the bits of
/// `CIAPI::Transform` (3×3 matrix in Lumen's order, crop corners in the transform's own domain); the rest is derived for the UI.
/// </summary>
public sealed record PhotoTransform
{
    /// <summary>Quarter turns clockwise of the picture relative to the capture: 0, 90, 180, 270.</summary>
    public int TurnDegrees { get; init; }
    /// <summary>Mirrored left ↔ right after the turn.</summary>
    public bool Mirrored { get; init; }
    /// <summary>The crop rectangle in the turned, uncropped picture (normalised); <see cref="NormRect.Full"/> = not cropped.</summary>
    public NormRect Crop { get; init; } = NormRect.Full;
    /// <summary>Whether anything is set at all (the identity transform, not cropped).</summary>
    public bool IsIdentity { get; init; } = true;
    /// <summary>The aspect ratio of the capture's frame the transform is defined on (4:3 on the L16).</summary>
    public int AspectWidth { get; init; } = 4;
    public int AspectHeight { get; init; } = 3;
    /// <summary>Lumen's matrix, Lumen's order (x' = m0·x + m3·y + m6, y' = m1·x + m4·y + m7).</summary>
    public IReadOnlyList<float> Matrix { get; init; } = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
    /// <summary>The crop corners as `CIAPI::Transform` holds them.</summary>
    public float CropX0 { get; init; }
    public float CropY0 { get; init; }
    public float CropX1 { get; init; } = 1f;
    public float CropY1 { get; init; } = 1f;
}

/// <summary>A complete, portable set of edit values (one photo's state, the clipboard, a preset). Which parts are meaningful is
/// <see cref="Groups"/>; the rest are the neutral values.</summary>
public sealed record EditValues
{
    /// <summary>The parts this set carries.</summary>
    public EditGroups Groups { get; init; } = EditGroups.All;
    /// <summary>Tone and colour (Lux's engine type: exposure, contrast, temperature / tint — null = the photo's own white balance —, vibrance,
    /// saturation, sharpening, shadows, highlights, clarity).</summary>
    public Adjustments Adjustments { get; init; } = Adjustments.Default;
    /// <summary>Crop, rotation and mirroring.</summary>
    public PhotoTransform Transform { get; init; } = new();
    /// <summary>The depth effect's f-number (null = all in focus) and focus distance in mm (null = Lumen's default focus).</summary>
    public float? FNumber { get; init; }
    public float? FocusMm { get; init; }
    /// <summary>Per-slider choice (Lumen's patch dialog ticks each tone property): when set, exactly these sliders are applied and the Tone /
    /// Colour / Sharpening flags of <see cref="Groups"/> are ignored; null = every slider of those groups.</summary>
    public IReadOnlySet<AdjustmentKey>? Sliders { get; init; }
}

/// <summary>One step of the non-depth edit history.</summary>
/// <param name="Index">Position in <see cref="IPhotoEdit.History"/> (0 = the oldest step).</param>
/// <param name="Label">"Exposure", "Rotate right", "Crop", "Paste", …</param>
/// <param name="Done">Applied (false = undone, available to redo).</param>
public sealed record EditHistoryStep(int Index, string Label, bool Done);

/// <summary>The histogram of the picture on screen (256 bins per channel).</summary>
/// <param name="Red">Counts of R = 0 … 255.</param>
/// <param name="Luminance">Lumen's own luminance channel (`ParamFloatArray 3`): bin ⌊(R + G + B) / 3⌋.</param>
/// <param name="Level">The level whose whole picture was counted (4 = Lumen's: the smallest level).</param>
/// <param name="Version">The content version it was computed for.</param>
public sealed record Histogram(int[] Red, int[] Green, int[] Blue, int[] Luminance, int Level, PixelSize Size, long Version)
{
    /// <summary>The largest bin over the three colour channels, for scaling bars.</summary>
    public int Peak => Math.Max(Red.Max(), Math.Max(Green.Max(), Blue.Max()));
}

/// <summary>Capture facts for the INFO panel.</summary>
public sealed record PhotoInfo
{
    public string Name { get; init; } = "";
    public DateTime? CaptureTime { get; init; }
    /// <summary>The full-size picture as shown (after the saved crop and rotation, and the display-only held turn while it applies) — the same
    /// as <see cref="IPhotoView.Geometry"/>'s level 0.</summary>
    public PixelSize FullSize { get; init; }
    /// <summary>What a full-size export makes (Lumen's "Resolution": the saved crop and rotation only; differs from <see cref="FullSize"/> only while
    /// the display-only held turn applies).</summary>
    public PixelSize ExportSize { get; init; }
    /// <summary>The photo's geometry is a decision of its own (the display-only held turn does not apply).</summary>
    public bool GeometryDecided { get; init; }
    public int FocalLengthMm { get; init; }
    public string? ExposureText { get; init; }
    public int? Iso { get; init; }
    public double? ExposureTimeSeconds { get; init; }
    /// <summary>The capture's exposure offset in EV (Lumen's "EV").</summary>
    public float? EvOffset { get; init; }
    public int StackFrames { get; init; } = 1;
    public string ReferenceModule { get; init; } = "";
    public int ModuleCount { get; init; }
    /// <summary>This photo's own white balance (the temperature / tint "as shot").</summary>
    public float AsShotTemperature { get; init; }
    public float AsShotTint { get; init; }
    public float AllInFocusFNumber { get; init; }
}

/// <summary>Where the edits stand on disk.</summary>
public enum SaveState
{
    /// <summary>Everything is saved.</summary>
    Saved = 0,
    /// <summary>A change waits for the automatic save (debounced).</summary>
    Pending = 1,
    /// <summary>The last save failed (<see cref="IPhotoEdit.SaveError"/>); retried at the next change and when the photo is closed.</summary>
    Failed = 2,
}

/// <summary>What an edit change was about (<see cref="IPhotoEdit.Changed"/>).</summary>
public enum EditChangeKind
{
    Adjustment = 0,
    Transform = 1,
    CropFrame = 2,
    History = 3,
    Saved = 4,
    Histogram = 5,
}

/// <summary>An edit changed.</summary>
public sealed class EditChangedEventArgs : EventArgs
{
    public required EditChangeKind Kind { get; init; }
    /// <summary>The slider, for <see cref="EditChangeKind.Adjustment"/>.</summary>
    public AdjustmentKey? Key { get; init; }
    /// <summary>The state after the change.</summary>
    public required EditValues State { get; init; }
    /// <summary>The history step the change added (or, for <see cref="EditChangeKind.History"/>, undid / redid); null when it added none (a drag
    /// frame, the crop frame moving, a save).</summary>
    public string? StepLabel { get; init; }
}

/// <summary>
/// The current edit state of the open photo: tone and colour sliders, crop / rotation / flips, undo / redo, automatic saving, histogram and
/// info. Every change is applied to the picture at once (<see cref="IPhotoView"/>) and saved automatically into the photo's context
/// database (next to the photo in <c>LuxImageContext</c>, or in the local store when its folder cannot be written), debounced
/// (<see cref="ViewerSettings.SaveDelayMs"/>). The library's mirror and thumbnail follow each save.
/// </summary>
public interface IPhotoEdit
{
    /// <summary>The sliders (ranges, defaults, steps, labels) in Lumen's panel order.</summary>
    IReadOnlyList<AdjustmentInfo> Adjustments { get; }
    /// <summary>The whole edit state now.</summary>
    EditValues Current { get; }
    /// <summary>The state the photo was opened with (what "revert" in a session would return to is <see cref="RevertToOriginal"/>, not this).</summary>
    EditValues Opened { get; }
    /// <summary>The value a slider shows (temperature / tint: the photo's own when "as shot").</summary>
    float GetValue(AdjustmentKey key);
    /// <summary>Temperature or tint follow the photo's own white balance (never moved, or reset).</summary>
    bool IsAsShot(AdjustmentKey key);

    /// <summary>Set a slider (a committed change: one undo step, saved). Out-of-range values are clamped to the slider's range.</summary>
    void SetValue(AdjustmentKey key, float value);
    /// <summary>A slider drag starts: following <see cref="DragTo"/> calls render at the drag level (coarser, within the frame budget) and make
    /// no undo step; <see cref="EndDrag"/> commits one undo step and refines the picture.</summary>
    void BeginDrag(AdjustmentKey key);
    void DragTo(AdjustmentKey key, float value);
    void EndDrag(AdjustmentKey key);
    /// <summary>Back to the unedited value (temperature / tint: as shot).</summary>
    void Reset(AdjustmentKey key);
    /// <summary>Reset a group (Lumen's per-section reset: TONE &amp; COLOR, GEOMETRY).</summary>
    void ResetGroups(EditGroups groups);
    /// <summary>Lumen's "revert to original": every tone, colour, crop and rotation edit removed (one undo step). Depth edits are untouched.</summary>
    void RevertToOriginal();
    /// <summary>Apply a set of values (a preset, a paste into the open photo): the groups it carries replace the current ones (one undo step).
    /// Returns false when its crop could not be applied (another aspect ratio) — the rest is applied.</summary>
    bool Apply(EditValues values, EditGroups groups);

    // ---- geometry (Lumen's GeometryEdits, the same operations on `CIAPI::Transform`)
    PhotoTransform Transform { get; }
    /// <summary>Turn 90° counter-clockwise / clockwise. As in Lumen the crop stays on the same part of the photo (it turns with it).</summary>
    void RotateLeft();
    void RotateRight();
    /// <summary>Mirror left ↔ right / top ↔ bottom of the shown picture.</summary>
    void FlipHorizontal();
    void FlipVertical();
    /// <summary>The way the camera was held (<see cref="CaptureItem.HeldOrientation"/>).</summary>
    PhotoOrientation HeldOrientation { get; }
    /// <summary>Whether <see cref="RotateAsHeld"/> would change anything (held turned, and the photo not already turned).</summary>
    bool CanRotateAsHeld { get; }
    /// <summary>Save the held orientation as a real rotation (the same as turning right / left by the held angle with "show as held" off), so exports
    /// follow it. With "show as held" on the picture shown does not change (the display turn becomes a saved one).</summary>
    void RotateAsHeld();
    /// <summary>Crop, rotation and flips back to none (Lumen's GEOMETRY reset).</summary>
    void ResetGeometry();

    /// <summary>The crop tool (Lumen: the crop button; zoom is fixed to fit while it is shown).</summary>
    CropState CropState { get; }
    /// <summary>Show the crop frame over the uncropped picture (the view switches to <see cref="PictureLayout.Uncropped"/>).</summary>
    void EnterCrop();
    /// <summary>The aspect preset of the frame; choosing one reshapes the frame as Lumen does (same area, centred, kept inside).</summary>
    CropAspect CropAspect { get; }
    void SetCropAspect(CropAspect aspect);
    /// <summary>The frame in the uncropped picture (normalised). Setting it (while dragging) only moves the frame; values are clamped to the
    /// picture. With an aspect preset the UI keeps the aspect (<see cref="FitCropFrame"/> helps).</summary>
    NormRect CropFrame { get; }
    void SetCropFrame(NormRect frame, bool dragging = false);
    /// <summary>The largest rectangle of the current preset's aspect centred in <paramref name="frame"/> (identity for Free).</summary>
    NormRect FitCropFrame(NormRect frame);
    /// <summary>Apply the frame (one undo step) and hide the tool. Lumen applies an open crop before any other tool.</summary>
    void ApplyCrop();
    /// <summary>Leave the tool without changing the crop.</summary>
    void CancelCrop();

    // ---- history (non-depth edits, this session)
    bool CanUndo { get; }
    bool CanRedo { get; }
    /// <summary>"Undo exposure", "Redo crop" — what the next undo / redo does.</summary>
    string? UndoLabel { get; }
    string? RedoLabel { get; }
    void Undo();
    void Redo();
    /// <summary>The whole history of this session: done steps oldest first, then the undone ones (redo order).</summary>
    IReadOnlyList<EditHistoryStep> History => Array.Empty<EditHistoryStep>();
    /// <summary>Steps applied now (= the index after the last done step).</summary>
    int HistoryPosition => 0;
    /// <summary>Undo or redo until <paramref name="position"/> steps are applied (the history panel's jump).</summary>
    void GoToHistory(int position) => throw new NotSupportedException();
    /// <summary>The photo's geometry is a decision of its own: the display-only "shown as held" turn does not apply. Set by the first geometry
    /// edit, cleared by "revert to original" / geometry reset.</summary>
    bool GeometryDecided => false;

    // ---- persistence
    /// <summary>A change has not been written yet.</summary>
    bool IsDirty { get; }
    SaveState SaveState { get; }
    /// <summary>Plain-language reason of the last failed save.</summary>
    string? SaveError { get; }
    /// <summary>Where the edits are kept (beside the photo / local store).</summary>
    ContextLocation SavedTo { get; }
    /// <summary>Write now (also done automatically, and when the session is disposed).</summary>
    Task SaveAsync(CancellationToken cancel = default);

    // ---- histogram and info
    /// <summary>The histogram of the edited picture (whole picture at level 4, as cut and turned); null until the first picture is made.</summary>
    Histogram? Histogram { get; }
    PhotoInfo Info { get; }

    /// <summary>Something changed (library thread).</summary>
    event EventHandler<EditChangedEventArgs>? Changed;
}

// ====================================================================================================================== copy / paste and presets

/// <summary>A named preset (stored in the library database).</summary>
public sealed record EditPreset(long Id, string Name, EditGroups Groups, EditValues Values, DateTime CreatedUtc);

/// <summary>The outcome of a paste or preset on many photos.</summary>
/// <param name="Written">Photos whose saved edits were changed.</param>
/// <param name="CropSkipped">Photos whose crop was left out (another aspect ratio) — Lumen's "Crop property was skipped n times".</param>
/// <param name="Failed">Photos that could not be written, with the reason (plain language).</param>
/// <param name="BatchId">The batch, for <see cref="IEditClipboard.UndoBatchAsync"/>.</param>
public sealed record EditBatchResult(int Written, int CropSkipped, IReadOnlyList<(string CaptureId, string Reason)> Failed, long BatchId);

/// <summary>
/// Copy / paste of edits between photos and named presets (Lumen's PRESETS panel and copy / paste adjustments). Pasting only writes saved
/// settings (no render): the photos' thumbnails are marked out of date and made again in the background. A paste or preset on photos is one
/// batch that can be undone as a whole (the last ten batches; while the library is open).
/// </summary>
public interface IEditClipboard
{
    /// <summary>The copied values, null when nothing was copied.</summary>
    EditValues? Clipboard { get; }
    /// <summary>The photo the clipboard was copied from.</summary>
    string? ClipboardSource { get; }
    /// <summary>Copy a photo's saved edits (the groups chosen; Lumen's "copy adjustments"). An open photo's unsaved changes are saved first.</summary>
    Task<EditValues> CopyAsync(string captureId, EditGroups groups, CancellationToken cancel = default);
    /// <summary>Copy values directly (e.g. from the open photo's <see cref="IPhotoEdit.Current"/>).</summary>
    void SetClipboard(EditValues values, string? sourceCaptureId = null);
    /// <summary>Paste the clipboard (its groups ∩ <paramref name="groups"/>) into photos. A photo open in the editor gets the change in its
    /// session too (undoable there).</summary>
    Task<EditBatchResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel = default);
    /// <summary>Paste with a per-slider choice: <paramref name="sliders"/> = exactly the tone / colour sliders to paste (null = by groups); the
    /// geometry and depth groups come from <paramref name="groups"/>.</summary>
    Task<EditBatchResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, IReadOnlySet<AdjustmentKey>? sliders, CancellationToken cancel = default)
        => PasteAsync(captureIds, groups, cancel);
    /// <summary>Reset groups on photos (Lumen's batch "reset adjustments").</summary>
    Task<EditBatchResult> ResetAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel = default);
    /// <summary>Undo a whole batch (every photo back to its saved edits before it). False when it is no longer available.</summary>
    Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel = default);
    /// <summary>The batches that can still be undone, newest first.</summary>
    IReadOnlyList<long> UndoableBatches { get; }

    IReadOnlyList<EditPreset> GetPresets();
    /// <summary>Create a preset (names unique, case-insensitive; an existing name throws <see cref="LibraryException"/>).</summary>
    Task<EditPreset> CreatePresetAsync(string name, EditValues values, EditGroups groups, CancellationToken cancel = default);
    Task<EditPreset> RenamePresetAsync(long presetId, string newName, CancellationToken cancel = default);
    /// <summary>Replace a preset's values (Lumen's overwrite).</summary>
    Task<EditPreset> UpdatePresetAsync(long presetId, EditValues values, EditGroups groups, CancellationToken cancel = default);
    Task DeletePresetAsync(long presetId, CancellationToken cancel = default);
    /// <summary>Apply a preset to photos (one undoable batch).</summary>
    Task<EditBatchResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel = default);
    /// <summary>Presets were created, renamed, changed or deleted.</summary>
    event EventHandler? PresetsChanged;
}

/// <summary>The viewer's tunables (also in <see cref="ILibrarySettings.GetLimits"/> under "Viewer", keys <c>viewer.*</c>).</summary>
public sealed record ViewerSettings
{
    /// <summary>Frame budget while a slider is dragged, ms (key <c>viewer.drag_budget_ms</c>, 15–500, default 60): the drag level is the
    /// finest level whose visible tiles the slider's stage restart is expected to finish within it (measured costs, updated as the session runs).</summary>
    public double DragBudgetMs { get; init; } = 60;
    /// <summary>The coarsest level a drag may fall back to (key <c>viewer.drag_coarsest_level</c>, 2–4, default 4).</summary>
    public int DragCoarsestLevel { get; init; } = 4;
    /// <summary>Delay after the last change before the automatic save, ms (key <c>viewer.save_delay_ms</c>, 0–10000, default 800).</summary>
    public int SaveDelayMs { get; init; } = 800;
    /// <summary>Memory the viewer keeps per open photo for fast slider changes, MB (key <c>viewer.display_cache_mb</c>, 128–16384,
    /// default 1024; about 25–32 MB per 512-pixel tile).</summary>
    public int DisplayCacheMb { get; init; } = 1024;
    /// <summary>Threads the viewer's renders use (key <c>viewer.threads</c>, 1 – 4 × processors, default = processors).</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;
}
