using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Controls;
using Lux.App.Develop;
using Lux.Library;
using HistoryKind = Lux.App.Develop.HistoryKind;

namespace Lux.App.ViewModels;

/// <summary>
/// One tone / colour row: label (double-click → default), slider, numeric value (typed values commit on Enter, Esc reverts). The slider's
/// drag goes to the session's drag calls — the picture follows at a lower level and the release makes one history step; nothing here waits.
/// </summary>
public sealed partial class AdjustmentRowViewModel : ObservableObject, IAdjustmentSliderTarget
{
    readonly Func<IDevelopSession?> _session;
    double _value;
    bool _dragging;

    public AdjustmentRowViewModel(AdjustmentSpec spec, Func<IDevelopSession?> session)
    {
        Spec = spec;
        _session = session;
        _value = spec.Default;
        _editText = spec.Format(spec.Default);
    }

    public AdjustmentSpec Spec { get; }
    public string Key => Spec.Key;
    public string Name => Spec.Name;
    public string Tip => (Spec.Tip ?? Spec.Name) + " — double-click the name to reset, click the number to type a value";
    public double Value => _value;
    public string ValueText => Spec.Format(_value);
    public bool IsDefault => Math.Abs(_value - Spec.Default) < Spec.Step / 2;
    public bool IsDragging => _dragging;
    /// <summary>Slider calls made while dragging (tests: the drag reaches the session).</summary>
    public int DragMoves { get; private set; }

    // ---- IAdjustmentSliderTarget
    public double Position => Spec.ToPosition(_value);
    public double DefaultPosition => Spec.ToPosition(Spec.Default);
    public double StepPosition => Spec.Scale == SliderScale.Log ? 0.005 : Spec.Step / (Spec.Max - Spec.Min) * (Spec.Decimals > 0 ? 5 : 1);

    public void BeginDrag()
    {
        if (_session() is not { } s) return;
        _dragging = true;
        s.BeginDrag(Key);
    }

    public void DragTo(double position)
    {
        if (_session() is not { } s) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_dragging) BeginDrag();
        var v = Spec.FromPosition(position);
        DragMoves++;
        Show(v);
        s.DragTo(Key, v);
        LongestMoveMs = Math.Max(LongestMoveMs, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    /// <summary>The longest time a drag move spent on the UI thread, ms (tests: the slider never waits for a render).</summary>
    public double LongestMoveMs { get; private set; }

    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _session()?.EndDrag(Key);
    }

    /// <summary>Double-click on the label: back to the default (one step).</summary>
    [RelayCommand]
    public void ResetToDefault()
    {
        if (_session() is not { } s) return;
        Show(Spec.Default);
        s.Reset(Key);
    }

    // ---- numeric entry
    [ObservableProperty] string _editText;
    [ObservableProperty] bool _hasError;
    /// <summary>The value field has the keyboard: refreshes from the session (a save finishing, a histogram) leave what is being typed alone.</summary>
    public bool IsEditing { get; set; }

    /// <summary>Enter in the value field: commit the typed value (clamped); an unreadable entry shows the error state and keeps the value.</summary>
    public bool CommitText()
    {
        if (!Spec.TryParse(EditText, out var v)) { HasError = true; return false; }
        HasError = false;
        EditText = Spec.Format(v);
        if (_session() is { } s && Math.Abs(v - _value) > 1e-12)
        {
            Show(v);
            s.SetValue(Key, v);
        }
        return true;
    }

    /// <summary>Esc in the value field: back to the shown value.</summary>
    public void RevertText()
    {
        HasError = false;
        EditText = ValueText;
    }

    /// <summary>The session's value (after an undo, a reset, a paste …); ignored while dragging.</summary>
    public void Refresh(double value)
    {
        if (_dragging) return;
        Show(value);
    }

    void Show(double v)
    {
        if (Math.Abs(v - _value) < 1e-12 && EditText == Spec.Format(v)) return;
        _value = v;
        if (!HasError && !IsEditing) EditText = Spec.Format(v);
        OnPropertyChanged(nameof(Value)); OnPropertyChanged(nameof(ValueText)); OnPropertyChanged(nameof(Position)); OnPropertyChanged(nameof(IsDefault));
    }
}

/// <summary>One row of the history panel: depth rows are marked; a row that cannot be reached now (being rebuilt, before a reset, an unreliable
/// saved step) is dimmed and says why.</summary>
public sealed class HistoryRowViewModel(int index, string label, bool isCurrent, bool isUndone, HistoryItem? item = null, bool reachable = true, string? why = null)
{
    public int Index { get; } = index;
    public string Label { get; } = label;
    /// <summary>"1", "2" ….</summary>
    public string Number { get; } = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool IsCurrent { get; } = isCurrent;
    /// <summary>A step after the current one (redo).</summary>
    public bool IsUndone { get; } = isUndone;
    public bool IsDepth { get; } = item?.IsDepth == true;
    public bool IsSaved { get; } = item?.Saved == true;
    public bool IsReachable { get; } = reachable;
    public HistoryKind Kind { get; } = item?.Kind ?? (index == 0 ? HistoryKind.Opened : HistoryKind.Tone);
    public string Tip { get; } = why ?? (isCurrent ? "The photo as it is now" : "Go back to this step") + (item?.Saved == true ? " · a depth edit from an earlier session (kept with the photo)" : "");
}

/// <summary>A row of the tools list.</summary>
public sealed partial class ToolViewModel(string name, string key, string tip, bool available, PageTool kind = PageTool.None) : ObservableObject
{
    public string Name { get; } = name;
    public string Key { get; } = key;
    public PageTool Kind { get; } = kind;
    /// <summary>Quick select's row: match depth with the quick-select applicator.</summary>
    public bool IsQuickSelect => Name == "Quick select";
    [ObservableProperty] string _tip = tip;
    public bool IsAvailable { get; } = available;
    [ObservableProperty] bool _isActive;
    /// <summary>The depth map is not ready: the row is dimmed (a click explains why and offers "Analyse now").</summary>
    [ObservableProperty] bool _isDimmed;
}

/// <summary>A preset button.</summary>
public sealed class PresetViewModel(PresetInfo preset, string description)
{
    public PresetInfo Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Tip { get; } = description;
}

/// <summary>An INFO row.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>An aspect choice of the crop (the crop bar highlights the one in use).</summary>
public sealed partial class AspectChoice(Viewer.AspectPreset preset) : ObservableObject
{
    public Viewer.AspectPreset Preset { get; } = preset;
    public string Label => Preset.Label;
    [ObservableProperty] bool _isSelected;
    public override string ToString() => Preset.Label;
}

/// <summary>A slider check box under its group (per-slider copy / paste, Lumen's patch dialog).</summary>
public sealed partial class SliderOption(string key, string name, string detail, bool on, bool available) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsAvailable { get; } = available;
    [ObservableProperty] bool _isOn = on && available;
}

/// <summary>A group check box of the copy / paste / preset dialog; tone, colour and sharpening list their sliders under it (the group is
/// ticked when all are, empty when none, mixed otherwise).</summary>
public sealed partial class GroupOption : ObservableObject
{
    bool _plainOn;

    public GroupOption(EditGroups group, string name, string detail, bool on, bool available, IReadOnlyList<SliderOption>? sliders = null)
    {
        Group = group; Name = name; Detail = detail; IsAvailable = available;
        Sliders = sliders ?? Array.Empty<SliderOption>();
        _plainOn = on && available;
        foreach (var x in Sliders) x.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsOn));
    }

    public EditGroups Group { get; }
    public string Name { get; }
    public string Detail { get; }
    public bool IsAvailable { get; }
    public IReadOnlyList<SliderOption> Sliders { get; }
    public bool HasSliders => Sliders.Count > 0;

    public bool? IsOn
    {
        get
        {
            if (!HasSliders) return _plainOn;
            var avail = Sliders.Where(x => x.IsAvailable).ToList();
            return avail.Count == 0 ? false : avail.All(x => x.IsOn) ? true : avail.All(x => !x.IsOn) ? false : null;
        }
        set
        {
            bool on = value != false && IsAvailable;
            if (!HasSliders) { if (_plainOn == on) return; _plainOn = on; OnPropertyChanged(); return; }
            foreach (var x in Sliders.Where(x => x.IsAvailable)) x.IsOn = on;
            OnPropertyChanged();
        }
    }
}

public enum PatchKind { Copy, Paste, Preset }

/// <summary>
/// Lumen's patch dialog (copy adjustments / paste adjustments / save as preset): the groups to include — tone, colour, sharpening, rotation
/// and flip, crop — each with what it holds; a name for a preset.
/// </summary>
public sealed partial class PatchDialogViewModel : ObservableObject
{
    readonly Func<PatchDialogViewModel, Task<string?>> _ok;
    readonly Action _cancel;

    public PatchDialogViewModel(PatchKind kind, string title, string subtitle, string okLabel, EditSet set, IReadOnlyList<AdjustmentSpec> specs,
                                EditGroups preselect, Func<PatchDialogViewModel, Task<string?>> ok, Action cancel, bool perSlider = false)
    {
        Kind = kind; Title = title; Subtitle = subtitle; OkLabel = okLabel; Set = set; _ok = ok; _cancel = cancel;
        PerSlider = perSlider;
        IReadOnlyList<SliderOption>? SlidersOf(EditGroups g) => !perSlider ? null : specs.Where(s => AdjustmentKeys.GroupOf(s.Key) == g)
            .Select(s => new SliderOption(s.Key, s.Name,
                set.Values.TryGetValue(s.Key, out var v) ? s.Format(v) : s.Key is AdjustmentKeys.Temperature or AdjustmentKeys.Tint ? "as shot" : "",
                (preselect & g) != 0 && (set.Sliders == null || set.Sliders.Contains(s.Key)),
                (set.Groups & g) != 0 && (set.Sliders == null || set.Sliders.Contains(s.Key))))
            .ToList();
        string Values(EditGroups g)
        {
            var parts = specs.Where(s => AdjustmentKeys.GroupOf(s.Key) == g)
                .Select(s => set.Values.TryGetValue(s.Key, out var v) ? $"{s.Name} {s.Format(v)}"
                    : s.Key is AdjustmentKeys.Temperature or AdjustmentKeys.Tint ? $"{s.Name} as shot" : s.Name).ToList();
            return string.Join(" · ", parts);
        }
        bool Has(EditGroups g) => (set.Groups & g) != 0;
        Groups = new List<GroupOption>
        {
            new(EditGroups.Tone, "Tone", perSlider ? "" : Values(EditGroups.Tone), (preselect & EditGroups.Tone) != 0, Has(EditGroups.Tone), SlidersOf(EditGroups.Tone)),
            new(EditGroups.Colour, "Colour", perSlider ? "" : Values(EditGroups.Colour), (preselect & EditGroups.Colour) != 0, Has(EditGroups.Colour), SlidersOf(EditGroups.Colour)),
            new(EditGroups.Sharpening, "Sharpening", perSlider ? "" : Values(EditGroups.Sharpening), (preselect & EditGroups.Sharpening) != 0, Has(EditGroups.Sharpening), SlidersOf(EditGroups.Sharpening)),
            new(EditGroups.DepthEffect, "Depth effect", Has(EditGroups.DepthEffect) ? set.DepthEffectText : "Not carried",
                (preselect & EditGroups.DepthEffect) != 0, Has(EditGroups.DepthEffect)),
            new(EditGroups.RotationAndMirror, "Rotation and flip", set.Geometry.HasTurnOrFlip ? Describe(set.Geometry) : "Not turned or flipped",
                (preselect & EditGroups.RotationAndMirror) != 0, Has(EditGroups.RotationAndMirror)),
            new(EditGroups.Crop, "Crop", set.Geometry.HasCrop ? $"{Math.Round(set.Geometry.Crop.Width * 100)} % × {Math.Round(set.Geometry.Crop.Height * 100)} % · {Viewer.AspectPreset.Of(set.Geometry.Aspect).Label}" : "Not cropped",
                (preselect & EditGroups.Crop) != 0, Has(EditGroups.Crop)),
        };
        foreach (var g in Groups) g.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(CanOk)); OnPropertyChanged(nameof(AllOn)); };
    }

    static string Describe(GeometryState g)
    {
        var p = new List<string>();
        if (g.TurnDegrees % 360 != 0) p.Add($"turned {g.TurnDegrees}°");
        if (g.Mirrored) p.Add("mirrored");
        var s0 = string.Join(", ", p);
        return s0.Length == 0 ? s0 : char.ToUpperInvariant(s0[0]) + s0[1..];
    }

    public PatchKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string OkLabel { get; }
    public EditSet Set { get; }
    public IReadOnlyList<GroupOption> Groups { get; }
    public bool IsPreset => Kind == PatchKind.Preset;
    [ObservableProperty] string _name = "";
    [ObservableProperty] string? _error;
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanOk));

    public bool PerSlider { get; }
    public EditGroups Chosen => Groups.Where(g => g.IsOn != false && g.IsAvailable).Aggregate(EditGroups.None, (a, g) => a | g.Group);

    /// <summary>The ticked sliders when the choice is narrower than whole groups; null = every slider of the chosen groups.</summary>
    public IReadOnlySet<string>? ChosenSliders
    {
        get
        {
            if (!PerSlider) return null;
            var tone = Groups.Where(g => g.HasSliders && g.IsOn != false).ToList();
            if (tone.All(g => g.IsOn == true) && (Set.Sliders == null)) return null;
            return tone.SelectMany(g => g.Sliders.Where(x => x.IsOn && x.IsAvailable).Select(x => x.Key)).ToHashSet();
        }
    }

    /// <summary>The set as chosen (groups and sliders).</summary>
    public EditSet ChosenSet => Set.Only(Chosen).OnlySliders(ChosenSliders);
    public bool CanOk => Chosen != EditGroups.None && (!IsPreset || !string.IsNullOrWhiteSpace(Name));
    public bool? AllOn
    {
        get
        {
            var avail = Groups.Where(g => g.IsAvailable).ToList();
            return avail.All(g => g.IsOn == true) ? true : avail.All(g => g.IsOn == false) ? false : null;
        }
        set { foreach (var g in Groups.Where(g => g.IsAvailable)) g.IsOn = value != false; }
    }

    [RelayCommand]
    async Task Ok()
    {
        if (IsPreset && string.IsNullOrWhiteSpace(Name)) { Error = "Type a name for the preset."; return; }
        if (Chosen == EditGroups.None) { Error = "Choose at least one group."; return; }
        Error = await _ok(this).ConfigureAwait(true);
    }

    [RelayCommand] void Cancel() => _cancel();
}
