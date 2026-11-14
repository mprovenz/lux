using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Lux.App.Viewer;

/// <summary>
/// Where an overlay sits above the picture (drawn bottom to top; pointer input offered top to bottom).
/// </summary>
public enum OverlayLayer
{
    /// <summary>Picture-attached shading: the quick-select mask, the focal-region shade.</summary>
    Mask = 100,
    /// <summary>Frames and guides: the crop frame with its handles and thirds.</summary>
    Guides = 200,
    /// <summary>Markers on the picture: the focus pin, the match-depth reference point.</summary>
    Markers = 300,
    /// <summary>The pointer's own drawing: the brush circle, the lasso being drawn.</summary>
    Cursor = 400,
}

/// <summary>A pointer event as an overlay sees it: in view, image and frame (normalised, oriented) coordinates.</summary>
public readonly record struct ViewerPointer(Point View, Point Image, Point Frame, KeyModifiers Modifiers, bool IsLeft, bool IsRight, int ClickCount);

/// <summary>
/// One layer drawn over the viewer's picture, with optional pointer and key input. The viewer offers a press to the visible overlays from
/// the top layer down; the first that takes it (returns true) receives the moves and the release (pointer capture). Unhandled presses pan
/// the picture. Overlays draw in view coordinates using the <see cref="ViewTransform"/> they are given (image ↔ frame ↔ view), so they
/// stay attached to the picture while it pans, zooms and turns.
/// </summary>
public abstract class ViewerOverlay
{
    bool _visible = true;

    /// <summary>The viewer showing this overlay (set when added).</summary>
    public PhotoViewer? Viewer { get; internal set; }
    public abstract OverlayLayer Layer { get; }

    public bool IsVisible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; Invalidate(); Viewer?.OverlayVisibilityChanged(); }
    }

    /// <summary>While true the viewer does not zoom or pan (the crop frame keeps the whole photo fitted, as Lumen does).</summary>
    public virtual bool LocksView => false;

    public abstract void Render(DrawingContext context, ViewTransform t, Rect bounds);

    /// <summary>A press; return true to take the pointer until release.</summary>
    public virtual bool OnPressed(ViewerPointer p, ViewTransform t) => false;
    public virtual void OnMoved(ViewerPointer p, ViewTransform t) { }
    public virtual void OnReleased(ViewerPointer p, ViewTransform t) { }
    /// <summary>The pointer hovers (no button): return a cursor to show, or null for the viewer's own.</summary>
    public virtual Cursor? CursorAt(Point view, ViewTransform t) => null;
    /// <summary>A key while the viewer has focus; return true when handled.</summary>
    public virtual bool OnKey(KeyEventArgs e) => false;
    /// <summary>The pointer hovers at <paramref name="view"/> (no button held, not panning), or left the viewer (null).</summary>
    public virtual void OnHover(Point? view, ViewTransform t) { }

    protected void Invalidate() => Viewer?.InvalidateVisual();
}
