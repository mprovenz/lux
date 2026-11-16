using System.Globalization;
using Lux.App.Viewer;
using Lux.Library;
using NormRect = Lux.Library.NormRect;

namespace Lux.App.Develop;

// App-side model of the Develop page: what the view models bind to. The fake (Fake/FakeDevelop.cs) and the adapter over the library's edit
// contract (ContractDevelop.cs, EditContracts.cs) implement it. It mirrors the contract's operations (drag begin / to / end, resets, Lumen's
// geometry operations and crop tool, groups for copy / paste / presets) and adds what the page needs on top: a history list with labels,
// formatted read-outs, and the tile source the viewer draws.

/// <summary>The tone and color adjustment keys, in the order the panel lists them.</summary>
public static class AdjustmentKeys
{
    public const string Exposure = "exposure", Contrast = "contrast", Highlights = "highlights", Shadows = "shadows", Clarity = "clarity",
        Temperature = "temperature", Tint = "tint", Vibrance = "vibrance", Saturation = "saturation", Sharpening = "sharpening";

    public static readonly IReadOnlyList<string> All = new[] { Exposure, Contrast, Highlights, Shadows, Clarity, Temperature, Tint, Vibrance, Saturation, Sharpening };

    /// <summary>The copy / paste group of a key (the library's groups: tone, color, sharpening).</summary>
    public static EditGroups GroupOf(string key) => key switch
    {
        Temperature or Tint or Vibrance or Saturation => EditGroups.Colour,
        Sharpening => EditGroups.Sharpening,
        _ => EditGroups.Tone,
    };

    public static AdjustmentKey ToContract(string key) => key switch
    {
        Exposure => AdjustmentKey.Exposure, Contrast => AdjustmentKey.Contrast, Temperature => AdjustmentKey.Temperature, Tint => AdjustmentKey.Tint,
        Vibrance => AdjustmentKey.Vibrance, Saturation => AdjustmentKey.Saturation, Sharpening => AdjustmentKey.Sharpening,
        Shadows => AdjustmentKey.Shadows, Highlights => AdjustmentKey.Highlights, Clarity => AdjustmentKey.Clarity,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "unknown adjustment"),
    };

    public static string FromContract(AdjustmentKey key) => key switch
    {
        AdjustmentKey.Exposure => Exposure, AdjustmentKey.Contrast => Contrast, AdjustmentKey.Temperature => Temperature, AdjustmentKey.Tint => Tint,
        AdjustmentKey.Vibrance => Vibrance, AdjustmentKey.Saturation => Saturation, AdjustmentKey.Sharpening => Sharpening,
        AdjustmentKey.Shadows => Shadows, AdjustmentKey.Highlights => Highlights, AdjustmentKey.Clarity => Clarity,
        _ => key.ToString().ToLowerInvariant(),
    };
}

public enum SliderScale { Linear, Log }

/// <summary>One adjustment: its plain name, range, default and how its value is written.</summary>
public sealed record AdjustmentSpec(string Key, string Name, double Min, double Max, double Default, double Step, int Decimals,
                                    string Unit = "", bool Signed = true, SliderScale Scale = SliderScale.Linear, string? Tip = null)
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>"+0.30", "−20", "0", "5600 K".</summary>
    public string Format(double v)
    {
        double r = Math.Round(v, Decimals);
        if (Math.Abs(r) < Math.Pow(10, -Decimals) / 2) r = 0;
        string n = Math.Abs(r).ToString("F" + Decimals, En);
        string sign = !Signed ? (r < 0 ? "−" : "") : r > 0 ? "+" : r < 0 ? "−" : "";
        return sign + n + Unit;
    }

    /// <summary>Read a typed value ("0.3", "+12", "−20", "5600", "5600 K"); clamped to the range. False when it is not a number.</summary>
    public bool TryParse(string? text, out double value)
    {
        value = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace('−', '-').Replace(',', '.');
        if (Unit.Trim().Length > 0 && t.EndsWith(Unit.Trim(), StringComparison.OrdinalIgnoreCase)) t = t[..^Unit.Trim().Length].Trim();
        if (!double.TryParse(t, NumberStyles.Float, En, out var v) || double.IsNaN(v) || double.IsInfinity(v)) return false;
        value = Clamp(v);
        return true;
    }

    public double Clamp(double v) => Math.Clamp(Math.Round(Math.Clamp(v, Min, Max) / Step) * Step, Min, Max);

    /// <summary>Slider position 0–1 of a value (log scale for temperature).</summary>
    public double ToPosition(double v) => Scale == SliderScale.Log
        ? (Math.Log(Math.Clamp(v, Min, Max)) - Math.Log(Min)) / (Math.Log(Max) - Math.Log(Min))
        : (Math.Clamp(v, Min, Max) - Min) / (Max - Min);

    public double FromPosition(double p) => Clamp(Scale == SliderScale.Log
        ? Math.Exp(Math.Log(Min) + Math.Clamp(p, 0, 1) * (Math.Log(Max) - Math.Log(Min)))
        : Min + Math.Clamp(p, 0, 1) * (Max - Min));

    /// <summary>Lumen's ranges (shadows, highlights and clarity are Lux's ±100); <paramref name="temperature"/> / <paramref name="tint"/> are the
    /// capture's own white balance (the defaults of those two sliders).</summary>
    public static IReadOnlyList<AdjustmentSpec> Standard(double temperature = 5600, double tint = 0) => new[]
    {
        new AdjustmentSpec(AdjustmentKeys.Exposure, "Exposure", -3, 3, 0, 0.01, 2, Tip: "Brightness in stops"),
        new AdjustmentSpec(AdjustmentKeys.Contrast, "Contrast", -100, 100, 0, 1, 0),
        new AdjustmentSpec(AdjustmentKeys.Highlights, "Highlights", -100, 100, 0, 1, 0, Tip: "Darker or brighter highlights (a Lux control; Lumen never offered it)"),
        new AdjustmentSpec(AdjustmentKeys.Shadows, "Shadows", -100, 100, 0, 1, 0, Tip: "Lift or deepen the shadows (a Lux control; Lumen never offered it)"),
        new AdjustmentSpec(AdjustmentKeys.Clarity, "Clarity", -100, 100, 0, 1, 0, Tip: "Local contrast (a Lux control; Lumen never offered it)"),
        new AdjustmentSpec(AdjustmentKeys.Temperature, "Temp", 2000, 20000, temperature, 10, 0, " K", Signed: false, Scale: SliderScale.Log,
                           Tip: "White balance: color temperature (the default is the photo's own)"),
        new AdjustmentSpec(AdjustmentKeys.Tint, "Tint", -150, 150, tint, 1, 0, Tip: "White balance: green (−) to magenta (+)"),
        new AdjustmentSpec(AdjustmentKeys.Vibrance, "Vibrance", -100, 100, 0, 1, 0),
        new AdjustmentSpec(AdjustmentKeys.Saturation, "Saturation", -100, 100, 0, 1, 0),
        new AdjustmentSpec(AdjustmentKeys.Sharpening, "Sharpen", -100, 100, 5, 1, 0),
    };
}

/// <summary>A photo's crop, rotation and flips as the editor shows them: quarter turns clockwise, mirrored left ↔ right after the turn, the
/// crop in the turned picture (normalized), and the crop's aspect preset.</summary>
public sealed record GeometryState(int TurnDegrees, bool Mirrored, NormRect Crop, CropAspect Aspect = CropAspect.Free)
{
    public static readonly GeometryState Identity = new(0, false, NormRect.Full);
    /// <summary>The level rotation in degrees, added to the quarter turns. Positive = the content turns counter-clockwise. 0 = none.</summary>
    public double LevelDegrees { get; init; }
    public bool HasLevel => LevelDegrees != 0;
    public bool IsIdentity => TurnDegrees % 360 == 0 && !Mirrored && Crop.IsFull() && !HasLevel;
    public bool HasCrop => !Crop.IsFull();
    public bool HasTurnOrFlip => TurnDegrees % 360 != 0 || Mirrored;
    public Turn Turn => new Turn((TurnDegrees / 90) & 3, false).Then(Mirrored ? new Turn(0, true) : Viewer.Turn.Identity);
}

/// <summary>Edits to copy, paste or keep as a preset: the groups they carry, the tone and color values, the geometry.</summary>
/// <param name="Native">The library's own value object this set was read from (kept so a round trip loses nothing); null when made here.</param>
public sealed record EditSet(EditGroups Groups, IReadOnlyDictionary<string, double> Values, GeometryState Geometry, object? Native = null)
{
    public static readonly EditSet Empty = new(EditGroups.None, new Dictionary<string, double>(), GeometryState.Identity);

    /// <summary>Per-slider choice (Lumen's patch dialog): exactly these sliders; null = every slider of the tone groups carried.</summary>
    public IReadOnlySet<string>? Sliders { get; init; }
    /// <summary>The depth effect when <see cref="Groups"/> has <see cref="EditGroups.DepthEffect"/>: the f-number (null = off) and the focus
    /// distance (null = the photo's default focus).</summary>
    public double? DepthFNumber { get; init; }
    public double? DepthFocusMm { get; init; }

    /// <summary>"Depth effect f/2.0 · focus 4.56 m", "Depth effect off".</summary>
    public string DepthEffectText => DepthFNumber is double f
        ? $"f/{f.ToString("0.0", CultureInfo.InvariantCulture)} · focus " + (DepthFocusMm is double mm ? DepthText.Distance(mm) : "default")
        : "Off" + (DepthFocusMm is double m2 ? $" · focus {DepthText.Distance(m2)}" : "");

    /// <summary>The same set limited to <paramref name="sliders"/> (null: unchanged).</summary>
    public EditSet OnlySliders(IReadOnlySet<string>? sliders) => sliders == null ? this : this with
    {
        Sliders = sliders,
        Values = Values.Where(kv => sliders.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>The same set limited to <paramref name="groups"/>.</summary>
    public EditSet Only(EditGroups groups) => this with
    {
        Groups = Groups & groups,
        Values = Values.Where(kv => (AdjustmentKeys.GroupOf(kv.Key) & groups) != 0 && (Sliders == null || Sliders.Contains(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value),
        Sliders = Sliders?.Where(k => (AdjustmentKeys.GroupOf(k) & groups) != 0).ToHashSet(),
    };

    /// <summary>"Exposure +0.30, contrast +12, rotation and flip, crop" (only the values that differ from the defaults).</summary>
    public string Describe(IReadOnlyList<AdjustmentSpec> specs)
    {
        var parts = new List<string>();
        foreach (var s in specs)
            if ((Groups & AdjustmentKeys.GroupOf(s.Key)) != 0 && (Sliders == null || Sliders.Contains(s.Key)) && Values.TryGetValue(s.Key, out var v) && Math.Abs(v - s.Default) > 1e-9)
                parts.Add($"{s.Name.ToLowerInvariant()} {s.Format(v)}");
        if ((Groups & EditGroups.DepthEffect) != 0) parts.Add("depth effect " + DepthEffectText.ToLowerInvariant());
        if ((Groups & EditGroups.RotationAndMirror) != 0 && Geometry.HasTurnOrFlip) parts.Add("rotation and flip");
        if ((Groups & EditGroups.Crop) != 0 && Geometry.HasCrop) parts.Add("crop");
        if (parts.Count == 0) return "No changes";
        var s0 = string.Join(", ", parts);
        return char.ToUpperInvariant(s0[0]) + s0[1..];
    }
}

/// <summary>A named preset.</summary>
public sealed record PresetInfo(long Id, string Name, EditGroups Groups, EditSet Set);

/// <summary>A paste / preset on several photos: how many changed, how many had their crop left out (another aspect), failures, and the batch
/// to undo.</summary>
public sealed record PasteResult(int Written, int CropSkipped, IReadOnlyList<string> Failures, long BatchId);

/// <summary>Where the edits stand on disk.</summary>
public enum SaveStatus { Nothing, Saved, Saving, Failed }

/// <param name="Where">"With the photo", "On this computer".</param>
public sealed record SaveInfo(SaveStatus Status, string? Where = null, string? Error = null);

/// <summary>256 bins per channel of the picture.</summary>
public sealed record HistogramData(int[] Red, int[] Green, int[] Blue)
{
    public int Peak => Math.Max(1, Math.Max(Red.Max(), Math.Max(Green.Max(), Blue.Max())));
}

/// <summary>What the picture area shows: the quality on screen, whether the requested quality is being made ("Processing image") with its
/// progress, and a failure.</summary>
public sealed record PictureStatus(ViewQuality OnScreen, bool Processing, double Progress, string? Error = null, bool Rendering = false)
{
    /// <summary>A plain line explaining the quality on screen when it is not what the kept renders suggest (a photo rendered from an imported
    /// Lumen session); null otherwise.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// An open capture in Develop: its picture (tiles), adjustments, geometry, history and save state. Members are called on the UI thread;
/// none of them waits on a render. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public interface IDevelopSession : IDisposable
{
    CaptureItem Item { get; }
    ITileSource Tiles { get; }
    IReadOnlyList<AdjustmentSpec> Specs { get; }
    double Get(string key);

    // ---- tone and color
    /// <summary>A slider drag starts: the following <see cref="DragTo"/> calls render at a coarser level and make no history step.</summary>
    void BeginDrag(string key);
    void DragTo(string key, double value);
    /// <summary>The drag ends: one history step (when the value moved), the picture is refined.</summary>
    void EndDrag(string key);
    /// <summary>A committed value (typed, a keyboard step): one history step.</summary>
    void SetValue(string key, double value);
    /// <summary>Back to the unedited value (double-click on the label).</summary>
    void Reset(string key);
    /// <summary>The tone and color section's reset (one step).</summary>
    void ResetToneAndColour();
    /// <summary>Lumen's "revert to original": every tone, color and geometry edit removed (one step, undoable).</summary>
    void RevertToOriginal();
    /// <summary>Paste or a preset into this photo: the groups of <paramref name="set"/> replace the current ones (one step). False when its
    /// crop could not be applied (another aspect ratio).</summary>
    bool Apply(EditSet set, string label);
    /// <summary>Every value and the geometry (copying).</summary>
    EditSet Snapshot();
    bool IsOriginal { get; }

    // ---- geometry (Lumen's operations; the picture is re-made turned / cropped by the library)
    GeometryState Geometry { get; }
    /// <summary>How the picture as shown is turned relative to the capture: the saved turn and flip and the display rotation.</summary>
    Turn PictureTurn { get; }
    /// <summary>The photo's geometry is a decision of its own: set by the first geometry edit — which bakes a held turn into
    /// the saved transform as one step named for the action — cleared by revert / geometry reset.</summary>
    bool GeometryDecided { get; }
    /// <summary>The photo is shown turned as the camera was held, and no geometry decision has been saved (exports keep the camera's own
    /// orientation until one is).</summary>
    bool ShownAsHeld { get; }
    void RotateLeft();
    void RotateRight();
    void FlipHorizontal();
    void FlipVertical();
    /// <summary>Whether the camera was held turned and the photo is not turned that way yet.</summary>
    bool CanRotateAsHeld { get; }
    void RotateAsHeld();
    /// <summary>The rotation that makes the photo level, from the gravity direction that the camera recorded, or the reason why there
    /// is none.</summary>
    LevelSuggestion LevelSuggestion { get; }
    /// <summary>Set the level rotation to the suggestion (one step). The crop becomes smaller so that it has no empty corners.</summary>
    void Level();
    void ResetGeometry();
    bool IsCropping { get; }
    /// <summary>The crop frame while cropping, normalized in the uncropped (turned) picture.</summary>
    NormRect CropFrame { get; }
    CropAspect CropAspect { get; }
    void EnterCrop();
    void SetCropAspect(CropAspect aspect);
    void SetCropFrame(NormRect frame, bool dragging);
    NormRect FitCropFrame(NormRect frame);
    void ApplyCrop();
    void CancelCrop();

    // ---- history (combined: tone, geometry and, with depth, the depth effect, depth edits and version switches)
    IReadOnlyList<string> History { get; }
    /// <summary>The same rows with their kind (depth rows are marked) and whether each can be revisited now. Saved depth steps from earlier
    /// sessions come first, then "Opened", then this session's steps.</summary>
    IReadOnlyList<HistoryItem> HistoryItems => History.Select((l, i) => new HistoryItem(l, i == 0 ? HistoryKind.Opened : HistoryKind.Tone)).ToList();
    /// <summary>Index into <see cref="History"/> of the state shown (0 = as opened).</summary>
    int HistoryPosition { get; }
    /// <summary>The first row as the panel names it: "Opened · original" when the photo had no saved edits,
    /// "Opened · with saved edits" otherwise; after "Clear history", "History cleared · original / with edits".</summary>
    string FirstRowLabel => "Opened";
    /// <summary>What the first row's state holds ("Saved edits: exposure −0.80 · crop · depth effect f/2.8 · depth edits"), null = nothing.</summary>
    string? FirstRowTip => null;
    /// <summary>"Clear history": the picture stays, the steps go (the panel's action).</summary>
    bool CanClearHistory => false;
    void ClearHistory() { }
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    void GoTo(int position);
    /// <summary>Whether a jump to <paramref name="position"/> is possible now (going back undoes every step on the way; a step being rebuilt, one
    /// before a depth reset or an unreliable saved step stops it).</summary>
    bool CanGoTo(int position) => position >= 0 && position < History.Count;

    // ---- saving, histogram, info, status
    SaveInfo Save { get; }
    Task SaveNowAsync();
    HistogramData? Histogram { get; }
    /// <summary>The INFO rows (label, value).</summary>
    IReadOnlyList<(string Label, string Value)> Info { get; }
    PictureStatus Status { get; }
    /// <summary>The highest quality the viewer may show (the quality selector's choice); null = the best the photo has.</summary>
    void SetQualityCap(ViewQuality? cap);
    /// <summary>The export of what is on screen (the export side panel passes these options for the open photo).</summary>
    ExportOptions ExportSettings(ExportOptions baseOptions);

    // ---- depth
    /// <summary>The photo's depth (effect, focus, tools, versions); null when the library cannot edit depth.</summary>
    IDepthSession? Depth => null;

    event Action? Changed;
}

/// <summary>Opens captures for editing; the clipboard, presets and pasting to captures that are not open.</summary>
public interface IDevelopService
{
    /// <summary>Whether editing is available (false: the library cannot edit yet; <see cref="UnavailableReason"/> says why).</summary>
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    Task<IDevelopSession> OpenAsync(CaptureItem item, CancellationToken cancel);
    IReadOnlyList<AdjustmentSpec> DefaultSpecs { get; }

    /// <summary>The copied edits; null when nothing was copied.</summary>
    EditSet? Clipboard { get; }
    string? ClipboardSource { get; }
    /// <summary>Copy the saved edits of a capture that is not open (the library's selection bar).</summary>
    Task<EditSet> CopyAsync(string captureId, string name, EditGroups groups, CancellationToken cancel);
    /// <summary>Copy values directly (the open photo's state).</summary>
    void SetClipboard(EditSet set, string sourceName, string? sourceCaptureId = null);
    /// <summary>Paste the clipboard (its groups ∩ <paramref name="groups"/>, its sliders) into captures (one batch).</summary>
    Task<PasteResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel);
    /// <summary>Copy / paste / presets can choose single sliders (else whole groups).</summary>
    bool SupportsPerSlider { get; }
    Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel);

    IReadOnlyList<PresetInfo> Presets { get; }
    Task<PresetInfo> CreatePresetAsync(string name, EditSet set, EditGroups groups, CancellationToken cancel);
    Task DeletePresetAsync(long presetId, CancellationToken cancel);
    /// <summary>Rename a preset (names unique, case-insensitive: an existing name throws <see cref="LibraryException"/>).</summary>
    Task<PresetInfo> RenamePresetAsync(long presetId, string newName, CancellationToken cancel);
    Task<PasteResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel);
    event Action? PresetsChanged;
    event Action? ClipboardChanged;
}

/// <summary>No editing (the library has no edit support).</summary>
public sealed class UnavailableDevelopService : IDevelopService
{
    public UnavailableDevelopService(string reason) => UnavailableReason = reason;
    public bool IsAvailable => false;
    public string? UnavailableReason { get; }
    public Task<IDevelopSession> OpenAsync(CaptureItem item, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public IReadOnlyList<AdjustmentSpec> DefaultSpecs => AdjustmentSpec.Standard();
    public EditSet? Clipboard => null;
    public string? ClipboardSource => null;
    public Task<EditSet> CopyAsync(string captureId, string name, EditGroups groups, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public void SetClipboard(EditSet set, string sourceName, string? sourceCaptureId = null) { }
    public Task<PasteResult> PasteAsync(IReadOnlyCollection<string> captureIds, EditGroups groups, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public bool SupportsPerSlider => false;
    public Task<bool> UndoBatchAsync(long batchId, CancellationToken cancel) => Task.FromResult(false);
    public IReadOnlyList<PresetInfo> Presets => Array.Empty<PresetInfo>();
    public Task<PresetInfo> CreatePresetAsync(string name, EditSet set, EditGroups groups, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public Task DeletePresetAsync(long presetId, CancellationToken cancel) => Task.CompletedTask;
    public Task<PresetInfo> RenamePresetAsync(long presetId, string newName, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public Task<PasteResult> ApplyPresetAsync(long presetId, IReadOnlyCollection<string> captureIds, CancellationToken cancel) => throw new LibraryException(UnavailableReason!);
    public event Action? PresetsChanged { add { } remove { } }
    public event Action? ClipboardChanged { add { } remove { } }
}
