using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>A format row of the export panel.</summary>
public sealed partial class FormatOption : ObservableObject
{
    readonly Action _changed;
    public FormatOption(string key, string name, string note, bool on, bool available, Action changed)
    {
        Key = key; Name = name; Note = note; _isOn = on; IsAvailable = available; _changed = changed;
    }
    public string Key { get; }
    public string Name { get; }
    public string Note { get; }
    public bool IsAvailable { get; }
    [ObservableProperty] bool _isOn;
    partial void OnIsOnChanged(bool value) => _changed();
}

public sealed record ChoiceOption(string Label, int Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The export side panel (S-Export, left half), opened from the library selection, the details panel or Develop: formats, size (the levels or
/// a custom long edge), edits, depth effect, tone style, folder and file names, the expected size and time; queues one export job per capture
/// (several captures form one group in the Queue). It reopens with the choices of the last export of this run of the app. When a file
/// already exists the setting decides (add a number by default; "ask" asks here before queueing).
/// </summary>
public sealed partial class ExportPanelViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly IReadOnlyList<CaptureItemViewModel> _items;
    readonly QueueViewModel _queue;
    readonly Action _close;
    readonly Action<string> _notify;
    readonly string? _defaultFolder;
    readonly Func<ExportOptions, Task<ExportOptions>>? _adjust;
    readonly Action? _openAnimate;
    bool _loading = true;

    /// <param name="adjust">The Develop page's hook: the open photo's export settings are the viewer's own (the same settings object).</param>
    /// <param name="openAnimate">Switch to the Animate panel for the same photos (animated effects have their own panel and preview).</param>
    public ExportPanelViewModel(AppServices s, IReadOnlyList<CaptureItemViewModel> items, QueueViewModel queue, Action close, Action<string> notify,
                                Func<ExportOptions, Task<ExportOptions>>? adjust = null, Action? openAnimate = null)
    {
        _s = s;
        _adjust = adjust;
        _openAnimate = openAnimate;
        _items = items;
        _queue = queue;
        _close = close;
        _notify = notify;
        var mem = s.PanelMemory;
        var last = mem.Export;
        bool On(string key, bool dflt) => last is null ? dflt : last.Formats.Contains(key);
        Formats = new ObservableCollection<FormatOption>
        {
            new("dng", "DNG", "Raw-style file for other editors", On("dng", true), true, Recalc),
            new("jpg", "JPG", "With tone and color adjustments", On("jpg", true), true, Recalc),
            new("jpg+depth", "JPG + depth", "Depth map embedded for apps that read it", On("jpg+depth", false), true, Recalc),
            new("jpg-hdr", "JPG + HDR gain map", "Brighter highlights on HDR displays", On("jpg-hdr", false), true, Recalc),
            new("tiff16", "TIFF 16-bit", "With tone and color adjustments, for editors", On("tiff16", false), true, Recalc),
            new("tiff-linear", "TIFF linear (HDR)", "Before the tone curve, keeps the highlights", On("tiff-linear", false), true, Recalc),
            new("hdr", "HDR", "Radiance, linear, for HDR tools", On("hdr", false), true, Recalc),
            new("ppm", "PPM", "8-bit image before tone and color, uncompressed", On("ppm", false), true, Recalc),
            new("depth", "Depth map", "Separate depth image (millimeters + preview)", On("depth", false), true, Recalc),
            new("stereo-sbs", "Stereo side by side", "Left and right eye from the two wide lenses", On("stereo-sbs", false), true, Recalc),
            new("stereo-anaglyph", "Stereo anaglyph", "One picture for red-cyan glasses", On("stereo-anaglyph", false), true, Recalc),
            new("stereo-wiggle", "Stereo wiggle", "Animation of the two eyes in turn", On("stereo-wiggle", false), true, Recalc),
            new("stereo-mpo", "Stereo MPO", "For 3D cameras, televisions and viewers", On("stereo-mpo", false), true, Recalc),
            new("mesh-glb", "3D photo (GLB)", "Relief for 3D viewers and the web", On("mesh-glb", false), true, Recalc),
            new("pointcloud-ply", "Point cloud (PLY)", "Colored points from the depth map", On("pointcloud-ply", false), true, Recalc),
        };
        Sizes = SizeChoices(items);
        _defaultFolder = s.Library.Settings.DefaultExportFolder;
        _folder = last?.OutputFolder ?? _defaultFolder ?? DefaultFolderDisplay;
        _size = Sizes.FirstOrDefault(x => x.Value == mem.ExportSizeChoice) ?? Sizes[0];
        _customEdge = (mem.ExportCustomEdge ?? 2048).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _edits = EditChoices[last is { UseSavedEdits: false } ? 1 : 0];
        _depthEffect = DepthChoices.FirstOrDefault(x => x.Value == mem.ExportDepthChoice) ?? DepthChoices[0];
        _fNumber = mem.ExportFNumber;
        _toneStyle = ToneChoices[last?.NewerToneStyle switch { true => 1, false => 2, _ => 0 }];
        _orientation = OrientationChoices[(last?.Unturned ?? s.Library.Settings.ExportUnturned) ? 1 : 0];
        _fileName = last?.FileNamePattern ?? "{name}";
        _embedColorProfile = last?.EmbedColorProfile ?? false;
        _tiffColorSpace = TiffColorChoices[last?.TiffColorSpace switch { "p3" => 1, "adobergb" => 2, _ => 0 }];
        _keepDngHighlights = last?.DngKeepHighlights ?? false;
        _jpegQuality = last?.JpegQuality ?? LumenJpegQuality;
        _teleCheck = last?.TeleCheck ?? false;
        _loading = false;
        Recalc();
    }

    public static string DefaultFolderDisplay => "~/Pictures/Lux Export";

    public string Title => _items.Count == 1 ? $"Export {_items[0].Name}" : $"Export {Text.Number(_items.Count)} captures";
    public string NamesLine
    {
        get
        {
            if (_items.Count == 1) return Text.LongDateTime(_items[0].Item.CaptureTime);
            var shown = _items.Take(4).Select((it, i) => i == 0 ? it.Name : it.ShortName).ToList();
            string s = string.Join(", ", shown);
            return _items.Count > 4 ? $"{s} and {Text.Number(_items.Count - 4)} more" : s;
        }
    }

    public ObservableCollection<FormatOption> Formats { get; }

    public IReadOnlyList<ChoiceOption> Sizes { get; }

    /// <summary>The size choices from the library's level sizes: exact when every selected capture has the same, else
    /// "each photo's own"; the last one is a custom long edge (value -1).</summary>
    static IReadOnlyList<ChoiceOption> SizeChoices(IReadOnlyList<CaptureItemViewModel> items)
    {
        static string Mp(Lux.Library.PixelSize p) => (p.Width * (double)p.Height / 1e6).ToString(p.Width * (double)p.Height >= 1e6 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
        var first = items.FirstOrDefault()?.Item;
        bool same = first is { LevelSizes.Count: >= 4 } && items.All(i => i.Item.LevelSizes.SequenceEqual(first.LevelSizes) && i.Item.Width == first.Width && i.Item.Height == first.Height);
        var custom = new ChoiceOption("Custom · long edge in pixels", -1);
        if (!same)
            return new ChoiceOption[] { new("Full size (each photo's own)", 0), new("13 MP (each photo's own)", 1), new("Smaller · a quarter of 13 MP", 2), new("Smallest · a sixteenth of 13 MP", 3), custom };
        var l = first!.LevelSizes;
        var full = first.Width > 0 ? new Lux.Library.PixelSize(first.Width, first.Height) : l[0];
        // shown the way the camera was held: the sizes as shown (the default export turns the file the same way)
        if (first.DisplayRotation is 90 or 270)
        {
            static Lux.Library.PixelSize T(Lux.Library.PixelSize p) => new(p.Height, p.Width);
            l = l.Select(T).ToList(); full = T(full);
        }
        return new ChoiceOption[]
        {
            new($"Full size · {full.Width} × {full.Height}", 0), new($"{Mp(l[1])} MP · {l[1].Width} × {l[1].Height}", 1),
            new($"{Mp(l[2])} MP · {l[2].Width} × {l[2].Height}", 2), new($"{Mp(l[3])} MP · {l[3].Width} × {l[3].Height}", 3), custom,
        };
    }
    public IReadOnlyList<ChoiceOption> EditChoices { get; } = new ChoiceOption[] { new("As saved for each capture", 1), new("Clean (ignore saved edits)", 0) };
    public IReadOnlyList<ChoiceOption> DepthChoices { get; } = new ChoiceOption[]
    {
        new("As saved for each capture", 0), new("Off (all in focus)", 1), new("Set aperture…", 2),
    };
    /// <summary>Lumen's tone style: as saved (an imported Lumen photo can carry the newer one), the newer, or the standard style.</summary>
    public IReadOnlyList<ChoiceOption> ToneChoices { get; } = new ChoiceOption[]
    {
        new("As saved for each capture", 0), new("Lumen's newer style", 1), new("Lumen's standard style", 2),
    };

    /// <summary>Orientation of photos shown the way the camera was held: as shown (turned upright, the default) or as
    /// captured (exactly as Lumen exports them). The default comes from Settings → Export.</summary>
    public IReadOnlyList<ChoiceOption> OrientationChoices { get; } = new ChoiceOption[]
    {
        new("As shown (upright)", 0), new("As captured, as Lumen does", 1),
    };
    [ObservableProperty] ChoiceOption _orientation;
    /// <summary>Some of the photos are shown the way the camera was held (the orientation choice matters for them).</summary>
    public bool HasTurnedPhotos => _items.Any(i => i.Item.DisplayRotation != 0);
    public string OrientationHint => !HasTurnedPhotos
        ? "Photos taken upright, or with a saved rotation, come out as shown either way."
        : Orientation.Value == 0
            ? (_items.Count == 1 ? "This photo is shown the way the camera was held; the file is turned the same way." : "Photos shown the way the camera was held are turned the same way in their files.")
            : (_items.Count == 1 ? "The file is written as the camera recorded it (sideways), as Lumen does." : "Files are written as the camera recorded them, as Lumen does.");
    partial void OnOrientationChanged(ChoiceOption value) => OnPropertyChanged(nameof(OrientationHint));

    [ObservableProperty] ChoiceOption _size;
    [ObservableProperty] string _customEdge;
    [ObservableProperty] ChoiceOption _edits;
    [ObservableProperty] ChoiceOption _depthEffect;
    [ObservableProperty] ChoiceOption _toneStyle;
    [ObservableProperty] double _fNumber = 2.8;
    [ObservableProperty] string _folder;
    [ObservableProperty] string _fileName;
    [ObservableProperty] string _estimate = "";
    [ObservableProperty] string _fileNamePreview = "";
    [ObservableProperty] string? _error;

    public bool IsSetAperture => DepthEffect.Value == 2;
    public bool IsCustomSize => Size.Value == -1;
    public string FNumberText => Text.FNumber((float)FNumber);
    public static string Note => "Tone and color adjustments are applied to JPG and TIFF files. DNG and HDR keep the unadjusted image, as in Lumen, so they can be developed later.";
    public string CollisionNote => _s.Library.Settings.ExportIfExists switch
    {
        ExportCollisionSetting.Ask => "If a file already exists you are asked first (Settings → Export).",
        ExportCollisionSetting.Replace => "Existing files with the same name are replaced (Settings → Export).",
        ExportCollisionSetting.Skip => "Photos whose files already exist are skipped (Settings → Export).",
        _ => "If a file already exists, the new one gets -2, -3 … (Settings → Export).",
    };
    public bool CanAnimate => _openAnimate != null;

    partial void OnSizeChanged(ChoiceOption value) { OnPropertyChanged(nameof(IsCustomSize)); Recalc(); }
    partial void OnCustomEdgeChanged(string value) => Recalc();
    partial void OnDepthEffectChanged(ChoiceOption value) { OnPropertyChanged(nameof(IsSetAperture)); Recalc(); }
    partial void OnFNumberChanged(double value) => OnPropertyChanged(nameof(FNumberText));
    partial void OnEditsChanged(ChoiceOption value) => Recalc();
    partial void OnToneStyleChanged(ChoiceOption value) => Recalc();
    partial void OnFileNameChanged(string value) => Recalc();

    public bool CanAdd => Formats.Any(f => f.IsOn && f.IsAvailable) && _items.Count > 0;
    /// <summary>The options of the last export queued (tests: the Develop page's settings reach the export).</summary>
    public ExportOptions? LastOptions { get; private set; }

    int? CustomEdgeValue => int.TryParse(CustomEdge, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 16 ? v : null;

    /// <summary>File size per capture and format at full size (measured: DNG ≈ 2.4 bytes a pixel, JPG ≈ 0.35, JPG + depth ≈ 0.5,
    /// HDR and the depth map 4, PPM 3), scaled to the chosen size; and the time from each photo's kept data.</summary>
    void Recalc()
    {
        if (_loading) return;
        var bpp = new Dictionary<string, double> { ["dng"] = 2.4, ["jpg"] = 0.35, ["jpg+depth"] = 0.5, ["jpg-hdr"] = 0.36, ["tiff16"] = 4.4, ["tiff-linear"] = 9, ["hdr"] = 4, ["ppm"] = 3, ["depth"] = 4.1 };
        var on = Formats.Where(f => f.IsOn && bpp.ContainsKey(f.Key)).Select(f => f.Key).ToList();
        double bytes = 0, secs = 0;
        bool depthRead = on.Contains("jpg+depth") || on.Contains("depth") || DepthEffect.Value == 2;
        foreach (var it in _items.Select(i => i.Item))
        {
            var full = it.Width > 0 ? (it.Width, it.Height) : it.LevelSizes.Count > 0 ? (it.LevelSizes[0].Width, it.LevelSizes[0].Height) : (8320, 6240);
            double px = (double)full.Item1 * full.Item2;
            int level = Size.Value;
            if (Size.Value == -1)
            {
                int le = CustomEdgeValue ?? 2048;
                double k = Math.Min(1, le / (double)Math.Max(full.Item1, full.Item2));
                px *= k * k;
                level = k > 0.5 ? 0 : k > 0.25 ? 1 : k > 0.125 ? 2 : 3;
            }
            else px /= Math.Pow(4, level);
            bytes += on.Sum(f => bpp[f]) * px;
            bool st = it.IsStacked; var c = it.CacheLevel;
            double t = level == 0 ? (c == CacheLevel.Full ? (st ? 10.6 : 9.7) : c >= CacheLevel.Geometry ? (st ? 99 : 32) : (st ? 111 : 49))
                     : level == 1 ? (c >= CacheLevel.Linear13 ? 3 : st ? 60 : 21)
                     : (c >= CacheLevel.Linear13 ? 2 : st ? 12 : 5);
            if (depthRead && level > 0) t += c >= CacheLevel.Geometry ? 4 : st ? 60 : 30;
            secs += t;
        }
        int needFull = Size.Value == 0 ? _items.Count(i => i.Item.CacheLevel != CacheLevel.Full) : 0;
        string render = Size.Value == 0
            ? (needFull == 0 ? "all from kept full-detail copies" : $"{Text.Number(needFull)} of {Text.Number(_items.Count)} rendered at full detail")
            : $"{Text.Number(_items.Count(i => i.Item.CacheLevel < CacheLevel.Linear13))} of {Text.Number(_items.Count)} need analysis first";
        Estimate = $"About {Text.Mb((long)bytes)} · about {Text.Duration(TimeSpan.FromSeconds(secs))} · {render}";
        var first = _items.FirstOrDefault()?.Item;
        if (first != null)
        {
            string stem = FileName.Replace("{name}", first.Name).Replace("{date}", first.CaptureTime?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "nodate")
                                  .Replace("{time}", first.CaptureTime?.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture) ?? "000000");
            string ext = on.FirstOrDefault() switch { "dng" => ".dng", "hdr" => ".hdr", "ppm" => ".ppm", "jpg+depth" => "_gdepth.jpg", "depth" => "_depth.f32", "jpg-hdr" => "_hdr.jpg", "tiff16" => ".tif", "tiff-linear" => "_linear.tif", _ => ".jpg" };
            if (on.Count == 0)   // only formats without a size estimate are ticked
                ext = Formats.FirstOrDefault(f => f.IsOn)?.Key switch { "stereo-sbs" => "_stereo-sbs.jpg", "stereo-anaglyph" => "_stereo-anaglyph.jpg", "stereo-wiggle" => "_stereo-wiggle.gif", "stereo-mpo" => "_stereo.mpo", "mesh-glb" => "_3d.glb", "pointcloud-ply" => "_cloud.ply", _ => ext };
            FileNamePreview = string.IsNullOrWhiteSpace(stem) ? "" : $"e.g. {stem}{ext}";
        }
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(JpgChosen)); OnPropertyChanged(nameof(Tiff16Chosen));
        OnPropertyChanged(nameof(DngChosen)); OnPropertyChanged(nameof(JpegFormatChosen));
    }

    [RelayCommand]
    async Task ChooseFolder()
    {
        var picked = await _s.Platform.PickFolderAsync("Export to folder").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(picked)) Folder = picked;
    }

    [RelayCommand] void Cancel() => _close();

    /// <summary>Close this panel and open the Animate panel for the same photos.</summary>
    [RelayCommand] void Animate() { _close(); _openAnimate?.Invoke(); }

    /// <summary>"DNG + JPG · full size · f/2.0".</summary>
    public string Describe()
    {
        var parts = new List<string> { string.Join(" + ", Formats.Where(f => f.IsOn && f.IsAvailable).Select(f => f.Name)) };
        parts.Add(Size.Value switch { 0 => "full size", 1 => "13 MP", 2 => "3 MP", 3 => "0.8 MP", _ => $"{CustomEdgeValue ?? 0} px" });
        if (DepthEffect.Value == 2) parts.Add(FNumberText);
        else if (DepthEffect.Value == 1) parts.Add("all in focus");
        if (Edits.Value == 0) parts.Add("clean");
        if (Orientation.Value == 1 && HasTurnedPhotos) parts.Add("as captured");
        if (EmbedColorProfile && JpgChosen) parts.Add("color profile");
        if (Tiff16Chosen && TiffColorSpace.Value != 0) parts.Add($"TIFF in {TiffColorSpace.Label}");
        if (KeepDngHighlights && DngChosen) parts.Add("DNG keeps highlights");
        if (JpegFormatChosen && JpegQualityValue != LumenJpegQuality) parts.Add($"JPEG quality {JpegQualityText}");
        if (TeleCheck) parts.Add("telephoto check");
        if (ToneStyle.Value == 1) parts.Add("newer tone style");
        else if (ToneStyle.Value == 2) parts.Add("standard tone style");
        return string.Join(" · ", parts);
    }

    ExportOptions Options() => new()
    {
        Formats = Formats.Where(f => f.IsOn && f.IsAvailable).Select(f => f.Key).ToList(),
        Level = Size.Value == -1 ? 0 : Size.Value,
        LongEdge = Size.Value == -1 ? CustomEdgeValue : null,
        OutputFolder = Folder == DefaultFolderDisplay && _defaultFolder == null ? null : ExpandHome(Folder),
        FileNamePattern = string.IsNullOrWhiteSpace(FileName) ? "{name}" : FileName.Trim(),
        UseSavedEdits = Edits.Value == 1,
        DepthEffectFNumber = DepthEffect.Value == 2 ? (float)Math.Round(FNumber, 1) : null,
        NewerToneStyle = ToneStyle.Value switch { 1 => true, 2 => false, _ => null },
        Unturned = Orientation.Value == 1,
        EmbedColorProfile = EmbedColorProfile && JpgChosen,
        TiffColorSpace = Tiff16Chosen ? TiffColorSpace.Value switch { 1 => "p3", 2 => "adobergb", _ => "srgb" } : null,
        DngKeepHighlights = KeepDngHighlights && DngChosen,
        JpegQuality = JpegFormatChosen && JpegQualityValue != LumenJpegQuality ? JpegQualityValue : null,
        TeleCheck = TeleCheck,
    };

    /// <summary>Checks the alignment of the telephoto lenses and corrects a lens that is clearly off. The default is off. The panel
    /// remembers the last choice.</summary>
    [ObservableProperty] bool _teleCheck;
    public static string TeleCheckNote => "Changes the merged picture slightly and takes about a second.";

    /// <summary>Keeps the highlights of the DNG above white. The default is off. The panel remembers the last choice.</summary>
    [ObservableProperty] bool _keepDngHighlights;
    /// <summary>DNG is ticked. The highlights option applies to that file only.</summary>
    public bool DngChosen => Formats.Any(f => f.IsOn && f.IsAvailable && f.Key == "dng");

    /// <summary>The JPEG quality of Lumen. An export with this value sends no quality, so its request is the default one.</summary>
    public const int LumenJpegQuality = 98;
    /// <summary>The JPEG quality, 1 to 100. The slider can give a fraction, so use <see cref="JpegQualityValue"/> to read it. The panel
    /// remembers the last choice.</summary>
    [ObservableProperty] double _jpegQuality;
    partial void OnJpegQualityChanged(double value) => OnPropertyChanged(nameof(JpegQualityText));
    /// <summary>The JPEG quality as a whole number from 1 to 100.</summary>
    public int JpegQualityValue => (int)Math.Clamp(Math.Round(JpegQuality), 1, 100);
    public string JpegQualityText => JpegQualityValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>A format that is a JPEG is ticked: JPG, JPG + depth, or JPG + HDR gain map. The quality applies to each of them.</summary>
    public bool JpegFormatChosen => Formats.Any(f => f.IsOn && f.IsAvailable && f.Key is "jpg" or "jpg+depth" or "jpg-hdr");

    /// <summary>The color spaces of the 16-bit TIFF. The panel remembers the last choice.</summary>
    public IReadOnlyList<ChoiceOption> TiffColorChoices { get; } = new ChoiceOption[] { new("sRGB", 0), new("Display P3", 1), new("Adobe RGB", 2) };
    [ObservableProperty] ChoiceOption _tiffColorSpace;
    /// <summary>The 16-bit TIFF is ticked. The color space choice applies to that file only.</summary>
    public bool Tiff16Chosen => Formats.Any(f => f.IsOn && f.IsAvailable && f.Key == "tiff16");

    /// <summary>Embeds the sRGB color profile in JPG files. The default is off. The panel remembers the last choice.</summary>
    [ObservableProperty] bool _embedColorProfile;
    /// <summary>A JPG format is ticked. The profile option applies to JPG files only.</summary>
    public bool JpgChosen => Formats.Any(f => f.IsOn && f.IsAvailable && f.Key is "jpg" or "jpg+depth");

    static string ExpandHome(string p) =>
        p.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]) : p;

    /// <summary>The question when files already exist and the setting says to ask: (existing, total) → the answer, or null = cancel. Set by
    /// the host (a dialog); tests answer directly.</summary>
    public Func<int, int, Task<ExistingFilePolicy?>>? AskAboutExisting { get; set; }

    [ObservableProperty] bool _isChecking;
    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(AddLabel));
    public string AddLabel => IsChecking ? "Checking files…" : "Add to queue";

    [RelayCommand]
    async Task AddToQueue()
    {
        if (!CanAdd) { Error = "Choose at least one format."; return; }
        if (string.IsNullOrWhiteSpace(Folder)) { Error = "Choose a folder for the files."; return; }
        if (Size.Value == -1 && CustomEdgeValue is null) { Error = "Type the long edge in pixels (16 or more)."; return; }
        var options = Options();
        var description = Describe();
        if (DepthEffect.Value == 1) options = options with { DepthEffectOff = true };
        var setting = _s.Library.Settings.ExportIfExists;
        options = options with { IfExists = setting switch { ExportCollisionSetting.Replace => ExistingFilePolicy.Overwrite, ExportCollisionSetting.Skip => ExistingFilePolicy.Skip, _ => ExistingFilePolicy.Rename } };
        var ids = _items.Select(i => i.Id).ToList();
        var lib = _s.Library;
        if (setting == ExportCollisionSetting.Ask)
        {
            // which files exist already, off the UI thread; then ask
            IsChecking = true;
            IReadOnlyList<ExportTarget> targets;
            try { targets = await Task.Run(() => lib.ExportTargets(ids, options)).ConfigureAwait(true); }
            finally { IsChecking = false; }
            int existing = targets.Count(t => t.Existing.Count > 0);
            if (existing > 0)
            {
                var answer = AskAboutExisting is null ? ExistingFilePolicy.Rename : await AskAboutExisting(existing, ids.Count).ConfigureAwait(true);
                if (answer is null) return;
                options = options with { IfExists = answer.Value };
            }
        }
        var mem = _s.PanelMemory;
        mem.Export = options; mem.ExportSizeChoice = Size.Value; mem.ExportCustomEdge = CustomEdgeValue; mem.ExportDepthChoice = DepthEffect.Value; mem.ExportFNumber = FNumber;
        var adjust = Edits.Value == 1 ? _adjust : null;
        _ = Task.Run(async () =>
        {
            // the Develop page: save the open photo's edits first and export exactly its settings
            if (adjust != null) options = await adjust(options).ConfigureAwait(false);
            LastOptions = options;
            return lib.Export(ids, options).ToList();
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (var id in t.Result) _queue.Describe(id, description); });
        }, TaskScheduler.Default);
        _notify(_items.Count == 1 ? "Export added to the queue" : $"{Text.Number(_items.Count)} exports added to the queue");
        _close();
    }
}

/// <summary>A one-field dialog (new album).</summary>
public sealed partial class PromptViewModel : ObservableObject
{
    readonly Func<string, Task<string?>> _submit;
    readonly Action _cancel;

    public PromptViewModel(string title, string label, string okLabel, Func<string, Task<string?>> submit, Action cancel)
    {
        Title = title; Label = label; OkLabel = okLabel; _submit = submit; _cancel = cancel;
    }

    public string Title { get; }
    public string Label { get; }
    public string OkLabel { get; }
    [ObservableProperty] string _value = "";
    [ObservableProperty] string? _error;

    [RelayCommand]
    async Task Ok()
    {
        if (string.IsNullOrWhiteSpace(Value)) { Error = "Type a name."; return; }
        Error = await _submit(Value.Trim()).ConfigureAwait(true);
    }

    [RelayCommand] void Cancel() => _cancel();
}

/// <summary>A yes / no dialog.</summary>
public sealed partial class ConfirmViewModel(string title, string text, string okLabel, Action ok, Action cancel) : ObservableObject
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public string OkLabel { get; } = okLabel;
    [RelayCommand] void Ok() => ok();
    [RelayCommand] void Cancel() => cancel();
}

/// <summary>One answer of a <see cref="ChoiceDialogViewModel"/>.</summary>
public sealed partial class DialogChoice(string label, int value, bool isDefault, Action<int> choose) : ObservableObject
{
    public string Label { get; } = label;
    public int Value { get; } = value;
    public bool IsDefault { get; } = isDefault;
    [RelayCommand] void Choose() => choose(Value);
}

/// <summary>A question with several answers and Cancel; completes <see cref="Answer"/> with the chosen value (null = canceled).</summary>
public sealed partial class ChoiceDialogViewModel : ObservableObject
{
    readonly TaskCompletionSource<int?> _tcs = new();
    public ChoiceDialogViewModel(string title, string text, IEnumerable<(string Label, int Value)> choices)
    {
        Title = title; Text = text;
        var list = choices.ToList();
        Choices = list.Select((c, i) => new DialogChoice(c.Label, c.Value, i == list.Count - 1, v => _tcs.TrySetResult(v))).ToList();
    }
    public string Title { get; }
    public string Text { get; }
    public IReadOnlyList<DialogChoice> Choices { get; }
    public Task<int?> Answer => _tcs.Task;
    [RelayCommand] void Cancel() => _tcs.TrySetResult(null);
    public void Choose(int value) => _tcs.TrySetResult(value);
}

/// <summary>The "files already exist" question of an export (setting "Ask").</summary>
public static class ExportQuestions
{
    public static async Task<ExistingFilePolicy?> AskAsync(IShell shell, int existing, int total)
    {
        var d = new ChoiceDialogViewModel(
            existing == 1 && total == 1 ? "This photo's files are already in the folder" : $"{existing} of {total} photos already have files in the folder",
            "Keep both adds -2, -3 … to the new files' names. Replace overwrites the files that are there. Skip leaves those photos out.",
            new[] { ("Skip those", (int)ExistingFilePolicy.Skip), ("Replace", (int)ExistingFilePolicy.Overwrite), ("Keep both", (int)ExistingFilePolicy.Rename) });
        shell.ShowDialog(d);
        try { return await d.Answer.ConfigureAwait(true) is int v ? (ExistingFilePolicy)v : null; }
        finally { shell.CloseDialog(d); }
    }
}
