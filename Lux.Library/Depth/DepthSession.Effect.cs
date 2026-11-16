using Lux.Engine.Pipeline.Dof;

namespace Lux.Library.Depth;

/// <summary>The depth effect, focus by click and the overlays (<see cref="IPhotoDepth"/>). The f-number and focus are edits of the photo's
/// state (<see cref="Edit.PhotoEditImpl.SetDepthEffect"/>): saved in its recipe, carried by copy / paste, one history step each.</summary>
internal sealed partial class DepthSession
{
    DepthOverlayMode _overlayMode = DepthOverlayMode.None;
    float _maxBlur = DofModel.DefaultMaxInFocusBlurPixels;
    float[] _shade = { 0.2f, 0.2f, 0.2f, 0.91f };
    float[] _selColour = { 1.0f, 0.25f, 0.55f, 0.45f };
    NormPoint? _focusPoint;

    public DepthEffectInfo Effect => _info;

    public DepthEffectState Current
    {
        get
        {
            var (f, focus) = _s.EditImpl.DepthEffect;
            bool on = f is float ff && ff < _info.AllInFocusFNumber;
            float? fo = focus is float x && x > 0f ? x : null;
            var snap = _snap;
            float? dflt = null;
            if (snap is not null) try { dflt = DofModel.DefaultFocusDepth(_s.Engine.Lri, snap.Cache.Current); } catch (InvalidOperationException) { }
            DepthRange? band = null;
            if (on && (fo ?? dflt) is float use)
            {
                float nsyn = DofGeometry.SyntheticAperture(_k, f!.Value);
                try { var (n, fa) = DofModel.InFocusRange(use, _k.FocalLength, _k.PixelPitch, nsyn, _maxBlur); band = new DepthRange(n, fa); }
                catch (ArgumentException) { }
            }
            return new DepthEffectState(on ? f : null, fo, dflt, band, _focusPoint) { IsOn = on };
        }
    }

    /// <summary>The value an f-number of the slider is stored as: "off" (null / at or above the all-in-focus one) is the base's own off (null
    /// when the photo has no saved f-number, else the all-in-focus number — an imported session's f-number stays overridden); below the full
    /// minimum it is raised to it.</summary>
    float? StoredFNumber(float? f)
    {
        if (f is null || float.IsNaN(f.Value) || f.Value >= _info.AllInFocusFNumber) return _s.EditImpl.BaseState.FNumber is null ? null : _info.AllInFocusFNumber;
        return Math.Max(f.Value, _info.FullMinimumFNumber);
    }

    public void SetFNumber(float? fNumber) => _s.EditImpl.SetDepthEffect(StoredFNumber(fNumber), null, true, false, "depth effect");
    public void BeginFNumberDrag() => _s.EditImpl.BeginDepthDrag();
    public void DragFNumber(float? fNumber) => _s.EditImpl.DragDepth(StoredFNumber(fNumber));
    public void EndFNumberDrag() => _s.EditImpl.EndDepthDrag();

    public void BeginFocusDrag() => _s.EditImpl.BeginDepthDrag();
    public void DragFocus(float focusMm)
    {
        if (!(focusMm > 0f)) throw new ArgumentOutOfRangeException(nameof(focusMm), "the focus distance must be greater than 0");
        _focusPoint = null;
        _s.EditImpl.DragDepthFocus(focusMm);
    }
    public void EndFocusDrag() => _s.EditImpl.EndDepthDrag();

    public void SetFocus(float? focusMm)
    {
        if (focusMm is float v && !(v > 0f)) throw new ArgumentOutOfRangeException(nameof(focusMm), "the focus distance must be greater than 0");
        float? stored = focusMm ?? (_s.EditImpl.BaseState.Focus is null ? null : -1f);
        _focusPoint = null;
        _s.EditImpl.SetDepthEffect(null, stored, false, true, "focus");
    }

    public Task<FocusPickResult> FocusAtAsync(NormPoint picture, bool commit = true, CancellationToken cancel = default)
    {
        var snap = _snap;
        if (snap is null) return Task.FromResult(new FocusPickResult(false, null, Status.Availability == DepthAvailability.Ready ? null : "The depth of this photo is not ready yet."));
        if (InsidePoint(picture) is not { } c) return Task.FromResult(new FocusPickResult(false, null, "The point is outside the photo."));
        var d = DepthAtCapture(snap.Cache.Current, c.X, c.Y);
        if (d is null) return Task.FromResult(new FocusPickResult(false, null, "There is no depth at this point."));
        // Lumen's focus tool: accepted only inside the range the renderer reports
        if (FocusableRange() is { } r && !(r.NearMm <= d.Value && d.Value <= r.FarMm))
            return Task.FromResult(new FocusPickResult(false, d, "This area cannot be brought into focus (it is shaded with the focal region)."));
        if (commit)
        {
            _s.EditImpl.SetDepthEffect(null, d, false, true, "focus");
            _focusPoint = picture;
        }
        return Task.FromResult(new FocusPickResult(true, d, null));
    }

    public void ResetDepthEffect()
    {
        _focusPoint = null;
        var b = _s.EditImpl.BaseState;
        _s.EditImpl.SetDepthEffect(b.FNumber is null ? null : _info.AllInFocusFNumber, b.Focus is null ? null : -1f, true, true, "reset depth effect");
    }

    // ---- overlays

    /// <summary>The overlay in effect: the quick-select applicator shows the selection.</summary>
    DepthOverlayMode EffectiveOverlay
    {
        get
        {
            lock (_toolLock) if (_tool == DepthTool.MatchDepth && _app == MatchApplicator.QuickSelect) return DepthOverlayMode.Selection;
            return _overlayMode;
        }
    }

    public DepthOverlayMode Overlay
    {
        get => _overlayMode;
        set { if (_overlayMode == value) return; _overlayMode = value; UpdateOverlay(committed: true, null); }
    }

    public float MaximumInFocusBlurPixels
    {
        get => _maxBlur;
        set
        {
            if (!(value >= 0.1f) || value > 10f) throw new ArgumentOutOfRangeException(nameof(value), "between 0.1 and 10 pixels");
            if (_maxBlur == value) return;
            _maxBlur = value;
            SetStatus(s => s with { FocusableRange = FocusableRange() });
            if (EffectiveOverlay == DepthOverlayMode.FocalRegion) UpdateOverlay(committed: true, null);
        }
    }

    public IReadOnlyList<float> FocalRegionColour
    {
        get => _shade.ToArray();
        set { if (value.Count != 4) throw new ArgumentException("four values (red, green, blue, opacity)"); _shade = value.ToArray(); if (EffectiveOverlay == DepthOverlayMode.FocalRegion) UpdateOverlay(true, null); }
    }

    public IReadOnlyList<float> SelectionColour
    {
        get => _selColour.ToArray();
        set { if (value.Count != 4) throw new ArgumentException("four values (red, green, blue, opacity)"); _selColour = value.ToArray(); UpdateOverlay(true, null); }
    }

    /// <summary>The overlay object for the present settings, depth snapshot, selection and stroke preview.</summary>
    DepthOverlay? BuildOverlay()
    {
        var mode = EffectiveOverlay; var snap = _snap;
        byte[]? sel; byte[]? prev;
        lock (_toolLock) { sel = mode == DepthOverlayMode.Selection ? _selectionMask : null; prev = _showPreview ? _previewMask : null; }
        Lux.Engine.Pipeline.Export.DepthImage? depth = null; (float, float) range = default;
        if (mode == DepthOverlayMode.FocalRegion && snap is not null && FocusableRange() is { } r) { depth = snap.Cache.Current; range = (r.NearMm, r.FarMm); }
        return DepthOverlay.Make(_lv, depth, range, _shade, sel, prev, _editSize, _selColour);
    }

    /// <summary>Put the overlay of the present settings in place: committed = everything re-rendered (a mode or color change), else only
    /// <paramref name="rect"/> (a stroke preview, the selection after a stroke).</summary>
    void UpdateOverlay(bool committed, NormRect? rect)
    {
        var ov = BuildOverlay();
        _s.EditImpl.SetOverlay(ov, committed, rect);
        RaiseChanged(DepthChangeKind.Overlay, committed ? NormRect.Full : rect, null);
    }
}
