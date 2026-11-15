using Avalonia;
using Avalonia.Media.Imaging;

namespace Lux.App.Viewer;

/// <summary>A tile of the picture pyramid: level L, column and row of L's tile grid.</summary>
public readonly record struct TileKey(int Level, int Col, int Row);

/// <summary>
/// One tile delivered by a <see cref="ITileSource"/>: level <see cref="Level"/>'s tile (<see cref="Col"/>, <see cref="Row"/>) of the picture
/// (smaller at the right and bottom edges), made for content <see cref="Version"/> of <see cref="Channel"/> and geometry
/// <see cref="GeometryVersion"/>. Bitmaps are made off the UI thread; the viewer owns the bitmap once it is accepted (and disposes it when
/// it is evicted or replaced).
/// </summary>
public sealed class TileImage
{
    public required int Channel { get; init; }
    public required int Level { get; init; }
    public required int Col { get; init; }
    public required int Row { get; init; }
    public required long Version { get; init; }
    public long GeometryVersion { get; init; }
    public required Bitmap Bitmap { get; init; }
    /// <summary>Rendered at the slider-drag level (coarser than asked); a refined tile follows when the drag ends.</summary>
    public bool IsDragPreview { get; init; }
    /// <summary><see cref="System.Diagnostics.Stopwatch"/> timestamp when the source handed the tile over (tile-to-screen latency).</summary>
    public long ReadyTicks { get; init; } = System.Diagnostics.Stopwatch.GetTimestamp();
    public TileKey Key => new(Level, Col, Row);
}

/// <summary>
/// What the viewer wants now: the tiles of <see cref="Level"/> covering <see cref="ImageRect"/> (level-0 picture pixels) for
/// <see cref="Channel"/>, of which <see cref="Needed"/> are the ones it does not hold at the current version (the coarsest level's tiles
/// among them when it lacks those). <see cref="ScreenPixels"/> is the device-pixel size the rectangle covers; <see cref="Interactive"/> is
/// set while the user pans or zooms. The newest request (highest <see cref="RequestId"/>) wins. A source may keep one request at a time
/// and cancel an older one of any channel, so the viewer asks for one channel at a time.
/// </summary>
public sealed record TileRequest(int Channel, int Level, Rect ImageRect, IReadOnlyList<TileKey> Needed, long RequestId)
{
    public Size ScreenPixels { get; init; }
    public bool Interactive { get; init; }
}

/// <summary>Tiles held for <paramref name="Channel"/> at an older version are valid at <paramref name="Version"/> unless they meet a changed region
/// of their level (level −1 = every level; rectangles normalised in the picture). From the library's changed regions.</summary>
public sealed record TileRevalidation(int Channel, long Version, long GeometryVersion, IReadOnlyList<(int Level, Lux.Library.NormRect Rect)> Changed);

/// <summary>Channels of a source: the edited picture, and the same picture without tone and colour adjustments.</summary>
public static class Channels
{
    public const int After = 0;
    public const int Before = 1;
}

/// <summary>
/// The picture pyramid the viewer draws (app side; the fake and the contract adapter implement it): the picture as shown — turned, flipped
/// and cropped by the source — level 0 at full size, coarser levels at their own sizes (not necessarily exact halvings). The last level is
/// the coarsest, which the viewer always asks for whole so something is on screen at once. Members are called on the UI thread; events may be
/// raised on any thread.
/// </summary>
public interface ITileSource
{
    /// <summary>Levels 0 … LevelCount − 1.</summary>
    int LevelCount { get; }
    /// <summary>The picture's size at a level.</summary>
    PixelSize LevelSize(int level);
    /// <summary>Tile edge in level pixels.</summary>
    int TileSize { get; }
    /// <summary>Increases when the picture's geometry changes (a turn, a flip, a crop, the crop tool shown or hidden): every held tile is
    /// dropped.</summary>
    long GeometryVersion { get; }
    /// <summary>The finest level that can come now (the quality on screen: 0 full detail, 1 the 13 MP render, 2 the preview).</summary>
    int FinestLevel { get; }
    /// <summary>When the latest geometry is the previous picture turned (same framing): that turn — the viewer then holds the old picture
    /// turned until the new one is in, also for changes the page did not start (an undo of a rotation). Null when not known.</summary>
    Turn? TurnFromPrevious => null;
    /// <summary>Whether the before channel exists.</summary>
    bool HasBefore { get; }
    /// <summary>The content version tiles of <paramref name="channel"/> are made for now.</summary>
    long Version(int channel);
    /// <summary>The oldest version still worth showing: tiles older than it are dropped on arrival (a committed change, a reset, a revert
    /// raise it; live slider moves do not, so the picture keeps moving while a drag outpaces the renders).</summary>
    long Floor(int channel);
    void Request(TileRequest request);
    /// <summary>A tile is ready (any thread).</summary>
    event Action<TileImage>? TileReady;
    /// <summary>A version, the geometry or the finest level changed (any thread): the viewer asks again.</summary>
    event Action? Changed;
    /// <summary>Held tiles outside a change stay valid at the new version (any thread); sources without region reports never raise it.</summary>
    event Action<TileRevalidation>? Revalidated { add { } remove { } }
}

public static class TileSourceExtensions
{
    /// <summary>Level 0's size: the picture at full size (the viewer's "image" coordinates).</summary>
    public static PixelSize ImageSize(this ITileSource s) => s.LevelSize(0);
}

/// <summary>What the page asks of its viewer (implemented by <see cref="PhotoViewer"/>; the view model holds it through this interface).</summary>
public interface IViewerCommands
{
    void ZoomToFit();
    void ZoomToFill();
    void ZoomToActual(Avalonia.Point? anchor = null, bool? animate = null);
    void ZoomToPercent(double percent, Avalonia.Point? anchor = null, bool? animate = null);
    void ZoomBy(double factor, Avalonia.Point? anchor = null, bool? animate = null);
    void ToggleFitActual(Avalonia.Point? anchor = null);
    void CenterImage();
    void ExpectGeometryChange(Turn turn, Lux.Library.NormRect? source = null, Lux.Library.NormRect? target = null);
    double ZoomPercent { get; }
    /// <summary>The zoom range in percent.</summary>
    (double Min, double Max) PercentRange { get; }
    bool IsFitted { get; }
    bool CanPan { get; }
    int LevelOnScreen { get; }
    int TargetLevel { get; }
    /// <summary>Visible slots of the target level and how many show their own current tile (the render progress).</summary>
    int TargetSlots { get; }
    int TargetSlotsReady { get; }
    /// <summary>The requested level is wholly on screen.</summary>
    bool TargetComplete { get; }
    event Action? ViewChanged;
}
