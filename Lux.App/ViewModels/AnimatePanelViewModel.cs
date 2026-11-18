using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>One effect of the Animate panel.</summary>
public sealed partial class EffectChoiceViewModel(AnimationEffectInfo info, Action<EffectChoiceViewModel> select) : ObservableObject
{
    /// <summary>New facts about the photo (its depth was computed): the reason and note follow.</summary>
    public void Update(AnimationEffectInfo next) { info = next; OnPropertyChanged(string.Empty); }
    public AnimationEffect Effect => info.Effect;
    public string Name => info.Name;
    public string Description => info.Description;
    public bool IsAvailable => info.Available;
    /// <summary>Why it cannot be made (shown under the name when unavailable).</summary>
    public string? Reason => info.Reason;
    public string? Note => info.Note;
    public string SubText => info.Available ? info.Description : info.Reason ?? "";
    [ObservableProperty] bool _isSelected;
    [RelayCommand] void Select() { if (IsAvailable) select(this); }
}

/// <summary>What a click on the picture picks.</summary>
public enum PickTarget { None, FocusFrom, FocusTo, Focus, Target }

/// <summary>
/// The "Animate…" side panel (library selection, details or Develop): effect, its parameters, size and file type with the expected size and
/// time, a live preview on the page's picture area, and "Add to queue" (one Queue job per photo, named after its file). Nothing here blocks
/// the UI thread: the library calls run on the thread pool and the preview renders in the background.
/// </summary>
public sealed partial class AnimatePanelViewModel : ObservableObject, IDisposable
{
    readonly AppServices _s;
    readonly IReadOnlyList<CaptureItemViewModel> _items;
    readonly QueueViewModel _queue;
    readonly Action _close;
    readonly Action<string> _notify;
    readonly Func<Task>? _beforeQueue;
    readonly Dictionary<string, CaptureItem> _fresh = new(StringComparer.Ordinal);
    readonly DispatcherTimer _debounce;
    IAnimationSource? _source;
    Task<IAnimationSource?>? _opening;
    CancellationTokenSource _estimateCts = new();
    bool _loading = true, _disposed;

    /// <param name="beforeQueue">The Develop page's hook: save the open photo's edits before the job reads them.</param>
    public AnimatePanelViewModel(AppServices s, IReadOnlyList<CaptureItemViewModel> items, QueueViewModel queue, Action close, Action<string> notify,
                                 Func<Task>? beforeQueue = null)
    {
        _s = s; _items = items; _queue = queue; _close = close; _notify = notify; _beforeQueue = beforeQueue;
        foreach (var it in items) _fresh[it.Id] = it.Item;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RefreshPreview(); };
        Player.Picked = OnPicked;
        var first = items[0].Item;
        // an effect is available when at least one of the photos can make it (a mixed selection notes how many can)
        var per = items.Select(i => AnimationEffects.For(i.Item)).ToList();
        foreach (var e in AnimationEffects.All)
        {
            var infos = per.Select(p => p.First(x => x.Effect == e)).ToList();
            int ok = infos.Count(x => x.Available);
            var info = infos.FirstOrDefault(x => x.Available) ?? infos[0];
            if (items.Count > 1 && ok > 0 && ok < items.Count) info = info with { Note = $"{ok} of {items.Count} photos can make it; the others are skipped." };
            Effects.Add(new EffectChoiceViewModel(ok > 0 ? info : infos[0], Choose));
        }
        Sizes = new ChoiceOption[] { new("520 px · small", 520), new("1040 px", 1040), new("1600 px", 1600), new("2080 px", 2080) };
        var formats = new List<ChoiceOption> { new("GIF · plays everywhere", (int)AnimationFormat.Gif), new("WebP · exact colors, smaller", (int)AnimationFormat.WebP),
                                                new("APNG · exact colors", (int)AnimationFormat.Apng) };
        if (s.Library.CanWriteVideo) { formats.Add(new("MP4 video", (int)AnimationFormat.Mp4)); formats.Add(new("WebM video", (int)AnimationFormat.WebM)); }
        Formats = formats;
        Repeats = new ChoiceOption[] { new("Forever", 0), new("Once", 1), new("3 times", 3) };
        Paths = new ChoiceOption[] { new("Sweep along the lenses", (int)AnimationPath.Sweep), new("Arc", (int)AnimationPath.Arc), new("Straight line", (int)AnimationPath.Line), new("Circle (orbit)", (int)AnimationPath.Orbit) };
        FogModes = new ChoiceOption[] { new("A fog bank comes forward", (int)Lux.Engine.Pipeline.Animate.FogMode.Bank), new("Fog thickens and thins", (int)Lux.Engine.Pipeline.Animate.FogMode.Rise) };
        SliceOrders = new ChoiceOption[] { new("Near to far", (int)Lux.Engine.Pipeline.Animate.SliceOrder.NearToFar), new("Far to near", (int)Lux.Engine.Pipeline.Animate.SliceOrder.FarToNear),
                                           new("Outward from the focus", (int)Lux.Engine.Pipeline.Animate.SliceOrder.FromFocus) };
        // the camera travel of the pan-and-zoom, in thousandths of the nearest distance: -1 = as far as the scene allows, 0 = a flat move
        Travels = new ChoiceOption[] { new("Automatic (as far as the scene allows)", -1), new("None (a flat move)", 0), new("1 % of the nearest distance", 10),
                                       new("2 %", 20), new("4 %", 40), new("6 %", 60), new("8 %", 80) };
        // the tilt-shift aperture, in thousandths: 0 = set from the scene
        Miniatures = new ChoiceOption[] { new("Automatic (from the scene)", 0), new("f/0.03 · strongest", 30), new("f/0.05", 50), new("f/0.1", 100), new("f/0.2", 200), new("f/0.5 · mildest", 500) };
        _fogMode = FogModes[0]; _sliceOrder = SliceOrders[0]; _travel = Travels[0]; _miniature = Miniatures[0];
        Dithers = new ChoiceOption[] { new("Fine pattern (steady)", (int)GifDither.Ordered), new("Diffusion (smoothest still)", (int)GifDither.Diffusion), new("None (smallest)", (int)GifDither.None) };
        // ---- the last settings of this run, else defaults
        var m = s.PanelMemory.Animation;
        _size = Sizes.FirstOrDefault(x => x.Value == (m?.LongEdge ?? 1040)) ?? Sizes[1];
        _format = Formats.FirstOrDefault(x => x.Value == (int)(m?.Format ?? AnimationFormat.Gif)) ?? Formats[0];
        _repeat = Repeats.FirstOrDefault(x => x.Value == (m?.Repeat ?? 0)) ?? Repeats[0];
        _path = Paths.FirstOrDefault(x => x.Value == (int)(m?.Path ?? AnimationPath.Sweep)) ?? Paths[0];
        _dither = Dithers.FirstOrDefault(x => x.Value == (int)(m?.GifDither ?? GifDither.Ordered)) ?? Dithers[0];
        _fNumber = m?.FNumber ?? 2.0;
        _frames = m?.Frames ?? AnimationSpec.DefaultFrames(m?.Effect ?? AnimationEffect.RackFocus);
        if (m is not null)
        {
            _fogMode = FogModes.FirstOrDefault(x => x.Value == (int)m.FogMode) ?? FogModes[0];
            _sliceOrder = SliceOrders.FirstOrDefault(x => x.Value == (int)m.SliceOrder) ?? SliceOrders[0];
            _fogDensity = m.FogDensity; _softness = m.SliceSoftness; _tilt = m.Tilt;
            _travel = Travels.FirstOrDefault(x => x.Value == (m.Flat ? 0 : m.Travel is double tr ? (int)Math.Round(tr * 1000) : -1)) ?? Travels[0];
            _miniature = Miniatures.FirstOrDefault(x => x.Value == (m.MiniatureFNumber is float mf ? (int)Math.Round(mf * 1000) : 0)) ?? Miniatures[0];
        }
        _amplitude = m?.AmplitudeMm ?? 71.49;
        _folder = m?.OutputFolder ?? s.PanelMemory.Export?.OutputFolder ?? s.Library.Settings.DefaultExportFolder ?? ExportPanelViewModel.DefaultFolderDisplay;
        var start = Effects.FirstOrDefault(x => x.Effect == (m?.Effect ?? AnimationEffect.RackFocus) && x.IsAvailable) ?? Effects.FirstOrDefault(x => x.IsAvailable) ?? Effects[0];
        _selected = start;
        start.IsSelected = true;
        if (AnimationSpec.PrefersVideo(start.Effect))
        {
            _pingPong = m?.PingPong ?? AnimationSpec.DefaultPingPong(start.Effect, AnimationPath.Sweep);
            if (m is null) PreferFormat(start.Effect);
        }
        _sizeWasSharp = _size.Value > SharpPreviewAbove;
        _loading = false;
        // the frames carry the photo's orientation (exports follow the view shown as held): no turn when drawing them;
        // only the thumbnail standing in before the first frame is turned as the library shows it
        Player.Rotation = 0;
        Player.PlaceholderRotation = first.DisplayRotation;
        _orientation = OrientationChoices[(m?.Unturned ?? s.Library.Settings.ExportUnturned) ? 1 : 0];
        if (_s.Thumbnails.TryGet(first.Id) is { } thumb) Player.Placeholder = thumb;
        else _ = LoadThumbAsync(first.Id);
        RaiseEffect();
        RefreshPreview();
    }

    async Task LoadThumbAsync(string id)
    {
        var b = await _s.Thumbnails.RequestAsync(id, CancellationToken.None).ConfigureAwait(true);
        if (b != null && !_disposed) Player.Placeholder = b;
    }

    public AnimationPlayerViewModel Player { get; } = new();
    public ObservableCollection<EffectChoiceViewModel> Effects { get; } = new();
    public IReadOnlyList<ChoiceOption> Sizes { get; }
    public IReadOnlyList<ChoiceOption> Formats { get; }
    public IReadOnlyList<ChoiceOption> Repeats { get; }
    public IReadOnlyList<ChoiceOption> Paths { get; }
    public IReadOnlyList<ChoiceOption> Dithers { get; }
    public IReadOnlyList<ChoiceOption> FogModes { get; }
    public IReadOnlyList<ChoiceOption> SliceOrders { get; }
    public IReadOnlyList<ChoiceOption> Travels { get; }
    public IReadOnlyList<ChoiceOption> Miniatures { get; }

    public string Title => _items.Count == 1 ? $"Animate {_items[0].Name}" : $"Animate {Text.Number(_items.Count)} photos";
    public string NamesLine => _items.Count == 1 ? Text.LongDateTime(_items[0].Item.CaptureTime)
        : $"Preview of {_items[0].Name}; the same settings for every photo";
    public string VideoNote => _s.Library.CanWriteVideo ? "" : "MP4 and WebM video need ffmpeg, which is not installed on this computer. GIF, WebP and APNG need nothing extra.";
    public bool HasVideoNote => !_s.Library.CanWriteVideo;

    [ObservableProperty] EffectChoiceViewModel _selected;
    [ObservableProperty] ChoiceOption _size;
    [ObservableProperty] ChoiceOption _format;
    [ObservableProperty] ChoiceOption _repeat;
    [ObservableProperty] ChoiceOption _path;
    [ObservableProperty] ChoiceOption _dither;
    [ObservableProperty] double _fNumber;
    [ObservableProperty] int _frames;
    [ObservableProperty] double _amplitude;
    [ObservableProperty] bool _singleView;
    [ObservableProperty] bool _pingPong = true;
    [ObservableProperty] ChoiceOption _fogMode;
    [ObservableProperty] double _fogDensity = 1.0;
    [ObservableProperty] ChoiceOption _sliceOrder;
    [ObservableProperty] double _softness = Lux.Engine.Pipeline.Animate.SliceLook.DefaultSoftness;
    [ObservableProperty] ChoiceOption _travel;
    [ObservableProperty] ChoiceOption _miniature;
    [ObservableProperty] double _tilt = 0.8;
    /// <summary>The point picked on the photo for the zoom-through or the pan-and-zoom (a fraction of the picture as shown); null = chosen
    /// for the photo.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetText), nameof(HasTarget))] (double X, double Y)? _target;
    /// <summary>Rack focus start / end and the aperture sweep's focus, meters as typed ("" = the photo's default).</summary>
    [ObservableProperty] string _focusFromText = "";
    [ObservableProperty] string _focusToText = "";
    [ObservableProperty] string _focusText = "";
    [ObservableProperty] PickTarget _pick;
    [ObservableProperty] string _folder;
    [ObservableProperty] string _estimate = "Estimating…";
    [ObservableProperty] string? _error;
    /// <summary>The preview's notes: why nothing or little changes with these settings on this photo.</summary>
    [ObservableProperty] string? _previewNote;
    /// <summary>How much detail the preview shows next to the file: said when its frames are smaller than the file's.</summary>
    [ObservableProperty] string? _previewDetailNote;
    /// <summary>The size of the file's frames, from the last estimate.</summary>
    (int W, int H)? _fileSize;

    /// <summary>Say plainly when the frames that play have less detail than the file will have (the lens effects, the long clips, a file
    /// larger than the preview's frames). The frames that play are the final ones: the note is about them, not about the still picture
    /// that shows while they are made.</summary>
    void UpdateDetailNote()
    {
        string? text = null;
        if (Player.Preview is { IsPlanned: true, IsFinal: true } p && _fileSize is { } file && p.FinalFrame(0) is { } f
            && Math.Max(f.Width, f.Height) * 10 < Math.Max(file.W, file.H) * 9)
            text = $"The preview's frames are {f.Width} × {f.Height}: they show less detail than the file, which is made at {file.W} × {file.H}.";
        if (PreviewDetailNote != text) PreviewDetailNote = text;
    }

    public bool IsRack => Selected.Effect == AnimationEffect.RackFocus;
    public bool IsAperture => Selected.Effect == AnimationEffect.ApertureSweep;
    public bool IsDepthEffect => IsRack || IsAperture;
    public bool IsParallax => Selected.Effect == AnimationEffect.ParallaxSweep;
    public bool IsZoom => Selected.Effect == AnimationEffect.ZoomThrough;
    public bool IsFog => Selected.Effect == AnimationEffect.Fog;
    public bool IsPan => Selected.Effect == AnimationEffect.PanZoom;
    public bool IsSlice => Selected.Effect == AnimationEffect.SliceReveal;
    public bool IsTilt => Selected.Effect == AnimationEffect.TiltShift;
    /// <summary>The effects with a target picked on the photo.</summary>
    public bool HasTargetPick => IsZoom || IsPan;
    /// <summary>The long clips: the zoom-through, the pan-and-zoom and the depth looks.</summary>
    public bool IsLongClip => AnimationSpec.PrefersVideo(Selected.Effect);
    public bool HasFrameCount => IsDepthEffect || IsParallax || IsLongClip;
    public int FramesMax => IsLongClip ? 240 : 48;
    public int FramesMin => IsLongClip ? 25 : 6;
    public bool HasTarget => Target is not null;
    public string TargetLabel => IsZoom ? "Zoom onto" : "Push in on";
    public string TargetText => Target is { } t ? $"{t.X * 100:0} % from the left, {t.Y * 100:0} % from the top"
                              : IsZoom ? "Automatic: the most telephoto detail" : "Automatic: where the camera focused";
    public string FogDensityText => $"{FogDensity * 100:0} %";
    public string SoftnessText => Softness < 0.005 ? "hard" : $"{Softness * 100:0} %";
    public string TiltText => Tilt < 0.005 ? "none" : $"{Tilt:0.0}";
    public bool IsPickingTarget => Pick == PickTarget.Target;
    public bool IsGif => Format.Value == (int)AnimationFormat.Gif;
    public string FNumberText => Text.FNumber((float)FNumber);
    public string FramesText => $"{Frames} frames";
    public string AmplitudeText => $"{Amplitude:0} mm";
    public string ApertureLabel => IsAperture ? "Opens to" : "Aperture";
    public string? EffectNote => Selected.Note;
    public string PickHint => Pick switch
    {
        PickTarget.FocusFrom => "Click the photo where the focus starts",
        PickTarget.FocusTo => "Click the photo where the focus ends",
        PickTarget.Focus => "Click the photo where it stays sharp",
        PickTarget.Target => IsZoom ? "Click the photo where the zoom ends" : "Click the photo where the push-in ends",
        _ => "",
    };
    public bool CanAdd => Selected.IsAvailable && !_disposed;

    // ---- orientation: a photo shown the way the camera was held is animated that way, unless asked for as captured
    public IReadOnlyList<ChoiceOption> OrientationChoices { get; } = new ChoiceOption[] { new("As shown (upright)", 0), new("As captured, as Lumen does", 1) };
    [ObservableProperty] ChoiceOption _orientation;
    partial void OnOrientationChanged(ChoiceOption value) => Changed();
    /// <summary>Some photo is shown the way the camera was held: the orientation choice matters.</summary>
    public bool HasTurnedPhotos => _items.Any(i => _fresh[i.Id].DisplayRotation != 0);

    /// <summary>The photos' facts changed (their depth was computed): the effects' notes follow.</summary>
    void RefreshEffects()
    {
        var lib = _s.Library; var ids = _items.Select(i => i.Id).ToList();
        _ = Task.Run(() => ids.Select(id => lib.Get(id)).Where(x => x != null).ToList()).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                foreach (var f in t.Result) _fresh[f!.Id] = f;
                var per = _items.Select(i => AnimationEffects.For(_fresh[i.Id])).ToList();
                foreach (var choice in Effects)
                {
                    var infos = per.Select(p => p.First(x => x.Effect == choice.Effect)).ToList();
                    int ok = infos.Count(x => x.Available);
                    var info = infos.FirstOrDefault(x => x.Available) ?? infos[0];
                    if (_items.Count > 1 && ok > 0 && ok < _items.Count) info = info with { Note = $"{ok} of {_items.Count} photos can make it; the others are skipped." };
                    choice.Update(info);
                }
                OnPropertyChanged(nameof(EffectNote));
                OnPropertyChanged(nameof(HasTurnedPhotos));
            });
        }, TaskScheduler.Default);
    }

    void Choose(EffectChoiceViewModel e)
    {
        foreach (var x in Effects) x.IsSelected = ReferenceEquals(x, e);
        Selected = e;
    }

    AnimationEffect _previous = AnimationEffect.RackFocus;
    AnimationFormat? _formatBeforeVideo;

    partial void OnSelectedChanged(EffectChoiceViewModel value)
    {
        if (_loading) return;
        Pick = PickTarget.None;
        // each kind of effect has its own frame count, play direction and file type: set them when the kind changes
        bool was = AnimationSpec.PrefersVideo(_previous), now = AnimationSpec.PrefersVideo(value.Effect);
        _loading = true;
        OnPropertyChanged(nameof(FramesMax)); OnPropertyChanged(nameof(FramesMin));   // the slider's range before its value
        if (was != now || now) { Frames = AnimationSpec.DefaultFrames(value.Effect); PingPong = now ? AnimationSpec.DefaultPingPong(value.Effect, AnimationPath.Sweep) : true; }
        if (now && !was) PreferFormat(value.Effect);
        else if (!now && was && _formatBeforeVideo is { } back) { Format = Formats.FirstOrDefault(x => x.Value == (int)back) ?? Format; _formatBeforeVideo = null; }
        if (_previous != value.Effect) Target = null;
        _loading = false;
        _previous = value.Effect;
        RaiseEffect(); Changed();
    }

    /// <summary>The long clips are written as MP4 when video can be written (the lossless file types get very large for them). The file
    /// type chosen before comes back with the other effects.</summary>
    void PreferFormat(AnimationEffect e)
    {
        if (AnimationEffects.PreferredFormat(e, _s.Library.CanWriteVideo) is not { } f || Format.Value == (int)f || AnimationOptions.IsVideo((AnimationFormat)Format.Value)) return;
        _formatBeforeVideo = (AnimationFormat)Format.Value;
        Format = Formats.FirstOrDefault(x => x.Value == (int)f) ?? Format;
    }

    partial void OnFogModeChanged(ChoiceOption value) => Changed();
    partial void OnFogDensityChanged(double value) { OnPropertyChanged(nameof(FogDensityText)); Changed(); }
    partial void OnSliceOrderChanged(ChoiceOption value) => Changed();
    partial void OnSoftnessChanged(double value) { OnPropertyChanged(nameof(SoftnessText)); Changed(); }
    partial void OnTravelChanged(ChoiceOption value) => Changed();
    partial void OnMiniatureChanged(ChoiceOption value) => Changed();
    partial void OnTiltChanged(double value) { OnPropertyChanged(nameof(TiltText)); Changed(); }
    partial void OnTargetChanged((double X, double Y)? value) => Changed();
    partial void OnSizeChanged(ChoiceOption value)
    {
        // The depth effects and the parallax get sharper preview frames for a file larger than 600 pixels. A size change that crosses
        // that limit changes the preview's final quality, so the preview is made again. Other size changes only change the estimate.
        bool sharp = value.Value > SharpPreviewAbove, was = _sizeWasSharp;
        _sizeWasSharp = sharp;
        Changed(previewToo: sharp != was && (IsDepthEffect || IsParallax));
    }
    const int SharpPreviewAbove = 600;
    bool _sizeWasSharp;
    partial void OnFormatChanged(ChoiceOption value) { OnPropertyChanged(nameof(IsGif)); Changed(previewToo: false); }
    partial void OnRepeatChanged(ChoiceOption value) => Changed(previewToo: false);
    partial void OnDitherChanged(ChoiceOption value) => Changed(previewToo: false);
    partial void OnPathChanged(ChoiceOption value) => Changed();
    partial void OnFNumberChanged(double value) { OnPropertyChanged(nameof(FNumberText)); Changed(); }
    partial void OnFramesChanged(int value) { OnPropertyChanged(nameof(FramesText)); Changed(); }
    partial void OnAmplitudeChanged(double value) { OnPropertyChanged(nameof(AmplitudeText)); Changed(); }
    partial void OnSingleViewChanged(bool value) => Changed();
    partial void OnPingPongChanged(bool value) => Changed();
    partial void OnFocusFromTextChanged(string value) { Changed(); UpdateMarkers(); }
    partial void OnFocusToTextChanged(string value) { Changed(); UpdateMarkers(); }
    partial void OnFocusTextChanged(string value) { Changed(); UpdateMarkers(); }
    public bool IsPickingFrom => Pick == PickTarget.FocusFrom;
    public bool IsPickingTo => Pick == PickTarget.FocusTo;
    public bool IsPickingFocus => Pick == PickTarget.Focus;
    partial void OnPickChanged(PickTarget value)
    {
        OnPropertyChanged(nameof(IsPickingFrom)); OnPropertyChanged(nameof(IsPickingTo)); OnPropertyChanged(nameof(IsPickingFocus)); OnPropertyChanged(nameof(IsPickingTarget));
        // A target is picked on the first frame, which shows the whole picture: the player holds that frame for as long as the pick is
        // open. It plays on by itself after the pick, also when the pick is canceled.
        Player.HoldAtStart = value == PickTarget.Target;
        Player.IsPicking = value != PickTarget.None;
        Player.PickHint = PickHint;
        OnPropertyChanged(nameof(PickHint));
    }

    void RaiseEffect()
    {
        foreach (var n in new[] { nameof(IsRack), nameof(IsAperture), nameof(IsDepthEffect), nameof(IsParallax), nameof(HasFrameCount), nameof(ApertureLabel),
                                  nameof(EffectNote), nameof(CanAdd), nameof(IsZoom), nameof(IsFog), nameof(IsPan), nameof(IsSlice), nameof(IsTilt), nameof(HasTargetPick),
                                  nameof(IsLongClip), nameof(FramesMax), nameof(FramesMin), nameof(TargetLabel), nameof(TargetText) })
            OnPropertyChanged(n);
        if (IsAperture && Math.Abs(FNumber - 2.8) < 0.01) FNumber = 2.0;
    }

    void Changed(bool previewToo = true)
    {
        if (_loading || _disposed) return;
        Error = null;
        if (previewToo) { _debounce.Stop(); _debounce.Start(); }
        else _ = RefreshEstimateAsync();
    }

    // ------------------------------------------------------------------------------------------------ options

    static float? Metres(string t) => float.TryParse(t.Trim().Replace("m", "", StringComparison.Ordinal).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v * 1000f : null;

    /// <summary>The options as set now.</summary>
    public AnimationOptions Options() => new()
    {
        Effect = Selected.Effect, Frames = Frames, PingPong = IsLongClip ? PingPong : PingPong ? null : false, Repeat = Repeat.Value, LongEdge = Size.Value,
        Target = HasTargetPick ? Target : null,
        FogMode = (Lux.Engine.Pipeline.Animate.FogMode)FogMode.Value, FogDensity = Math.Round(FogDensity, 2),
        SliceOrder = (Lux.Engine.Pipeline.Animate.SliceOrder)SliceOrder.Value, SliceSoftness = Math.Round(Softness, 2),
        Travel = Travel.Value > 0 ? Travel.Value / 1000.0 : null, Flat = Travel.Value == 0,
        MiniatureFNumber = Miniature.Value > 0 ? Miniature.Value / 1000f : null, Tilt = Math.Round(Tilt, 1),
        Format = (AnimationFormat)Format.Value, GifDither = (GifDither)Dither.Value,
        FNumber = (float)Math.Round(FNumber, 1),
        FocusFromMm = Metres(FocusFromText), FocusToMm = Metres(FocusToText), FocusMm = Metres(FocusText),
        Path = (AnimationPath)Path.Value, AmplitudeMm = Math.Round(Amplitude, 1), SingleView = SingleView, Unturned = Orientation.Value == 1,
        OutputFolder = Folder == ExportPanelViewModel.DefaultFolderDisplay && _s.Library.Settings.DefaultExportFolder == null ? null : ExpandHome(Folder),
    };

    static string ExpandHome(string p) =>
        p.StartsWith("~/", StringComparison.Ordinal) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]) : p;

    // ------------------------------------------------------------------------------------------------ preview and estimate

    Task<IAnimationSource?> SourceAsync()
    {
        return _opening ??= Task.Run(async () =>
        {
            try { return (IAnimationSource?)await _s.Library.OpenAnimationAsync(_items[0].Id).ConfigureAwait(false); }
            catch (Exception e) when (e is LibraryException or NotSupportedException)
            {
                Dispatcher.UIThread.Post(() => Error = e is NotSupportedException ? "Animations need a newer library." : e.Message);
                return null;
            }
        });
    }

    /// <summary>Start (or resume) the preview of the current options on the player.</summary>
    int _seq;

    public void RefreshPreview()
    {
        if (_disposed) return;
        var o = Options();
        int seq = ++_seq;
        bool available = Selected.IsAvailable;
        _ = Task.Run(async () =>
        {
            var src = await SourceAsync().ConfigureAwait(false);
            if (src == null || _disposed) return;
            if (!ReferenceEquals(_source, src)) src.AnalysisFinished += (_, _) => RefreshEffects();
            _source = src;
            if (!available || seq != Volatile.Read(ref _seq)) return;
            IAnimationPreview p;
            try { p = src.Preview(o); }
            catch (ObjectDisposedException) { return; }   // the panel closed meanwhile
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || seq != _seq) return;
                if (Player.Preview != null) Player.Preview.Changed -= OnPreviewChanged;
                Player.Bind(p);
                p.Changed += OnPreviewChanged;
                PreviewNote = NotesOf(p);
                UpdateDetailNote();
            });
            if (_disposed || seq != Volatile.Read(ref _seq)) { if (_disposed) p.Dispose(); return; }
            await RefreshEstimateAsync().ConfigureAwait(false);
        });
    }

    static string? NotesOf(IAnimationPreview p) => p.Notes.Count == 0 ? null : string.Join("\n", p.Notes);

    int _estimateTick;
    void OnPreviewChanged(object? sender, EventArgs e)
    {
        if (sender is IAnimationPreview np)
        {
            var text = NotesOf(np);
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !ReferenceEquals(Player.Preview, np)) return;
                if (PreviewNote != text) PreviewNote = text;
                UpdateDetailNote();
            });
        }
        if (sender is not IAnimationPreview p) return;
        // A finer estimate as the first frames arrive (every few frames), once when they are complete and once when the preview is final.
        // A second pass raises many more changes: they need no new estimate.
        bool complete = p.IsComplete && !ReferenceEquals(Interlocked.Exchange(ref _completeSeen, p), p);
        bool final = p.IsFinal && !ReferenceEquals(Interlocked.Exchange(ref _finalSeen, p), p);
        if (complete || final || (!p.IsComplete && Interlocked.Increment(ref _estimateTick) % 6 == 0)) _ = RefreshEstimateAsync();
        if (complete) RefreshEffects();
    }
    IAnimationPreview? _completeSeen, _finalSeen;

    async Task RefreshEstimateAsync()
    {
        var src = _source;
        AnimationOptions o = await Dispatcher.UIThread.InvokeAsync(Options);
        _estimateCts.Cancel();
        var cts = _estimateCts = new CancellationTokenSource();
        if (src == null) { await Dispatcher.UIThread.InvokeAsync(() => Estimate = "Estimating…"); return; }
        try
        {
            var est = await src.EstimateAsync(o, cts.Token).ConfigureAwait(false);
            int n = _items.Count;
            string text = $"About {Text.Mb(est.Bytes * n)} · {est.Width} × {est.Height} · {est.PlayedFrames} frames"
                        + $" · about {Text.Duration(TimeSpan.FromSeconds(est.Time.TotalSeconds * n))} to make" + (est.Basis == "typical size" ? " (typical size)" : "");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                Estimate = text;
                _fileSize = (est.Width, est.Height);
                UpdateDetailNote();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is LibraryException or InvalidOperationException) { await Dispatcher.UIThread.InvokeAsync(() => Estimate = "No estimate: " + e.Message); }
    }

    // ------------------------------------------------------------------------------------------------ picking focus on the photo

    [RelayCommand] void PickFrom() => Pick = Pick == PickTarget.FocusFrom ? PickTarget.None : PickTarget.FocusFrom;
    [RelayCommand] void PickTo() => Pick = Pick == PickTarget.FocusTo ? PickTarget.None : PickTarget.FocusTo;
    [RelayCommand] void PickFocus() => Pick = Pick == PickTarget.Focus ? PickTarget.None : PickTarget.Focus;
    [RelayCommand] void PickTargetPoint() => Pick = Pick == PickTarget.Target ? PickTarget.None : PickTarget.Target;
    [RelayCommand] void ClearTarget() { Pick = PickTarget.None; Target = null; }

    readonly Dictionary<PickTarget, (double X, double Y)> _picked = new();

    void OnPicked(double x, double y)
    {
        var target = Pick;
        if (target == PickTarget.None) return;
        Pick = PickTarget.None;
        if (target == PickTarget.Target)
        {
            // the first frame of the zoom-through is the whole picture; the pan-and-zoom starts on its middle, enlarged by the start zoom
            double k = IsPan ? Lux.Engine.Pipeline.Animate.PanZoomMove.ZoomStart : 1.0;
            Target = (Math.Clamp(0.5 + (x - 0.5) / k, 0, 1), Math.Clamp(0.5 + (y - 0.5) / k, 0, 1));
            return;
        }
        _picked[target] = (x, y);
        UpdateMarkers();
        _ = Task.Run(async () =>
        {
            var src = await SourceAsync().ConfigureAwait(false);
            if (src == null) return;
            try
            {
                float mm = await src.DepthAtAsync(x, y).ConfigureAwait(false);
                string m = (mm / 1000f).ToString("0.00", CultureInfo.InvariantCulture);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (target == PickTarget.FocusFrom) FocusFromText = m; else if (target == PickTarget.FocusTo) FocusToText = m; else FocusText = m;
                });
            }
            catch (Exception e) when (e is LibraryException or InvalidOperationException or ExportRequestException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Error = "No depth at that point: " + e.Message);
            }
        });
    }

    void UpdateMarkers()
    {
        var list = new List<PlayerMarker>();
        void Add(PickTarget t, string text, string label) { if (text.Length > 0 && _picked.TryGetValue(t, out var p)) list.Add(new PlayerMarker(p.X, p.Y, label)); }
        if (IsRack) { Add(PickTarget.FocusFrom, FocusFromText, "Start"); Add(PickTarget.FocusTo, FocusToText, "End"); }
        if (IsAperture) Add(PickTarget.Focus, FocusText, "Focus");
        Player.Markers = list;
    }

    /// <summary>Tests: pick as a click on the picture would (a point of the picture as shown, turned like the player shows it).</summary>
    public void ClickPicture(double x, double y) => Player.ClickShown(x, y);

    // ------------------------------------------------------------------------------------------------ queue

    [RelayCommand]
    async Task ChooseFolder()
    {
        var picked = await _s.Platform.PickFolderAsync("Save animations to folder").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(picked)) Folder = picked;
    }

    [RelayCommand] void Cancel() => _close();

    /// <summary>The options of the last animation queued (tests).</summary>
    public AnimationOptions? LastOptions { get; private set; }

    [RelayCommand]
    void AddToQueue()
    {
        if (!CanAdd) { Error = "This effect cannot be made from this photo."; return; }
        if ((IsRack && (FocusFromText.Length > 0 && Metres(FocusFromText) is null || FocusToText.Length > 0 && Metres(FocusToText) is null))
            || (IsAperture && FocusText.Length > 0 && Metres(FocusText) is null))
        { Error = "Type distances in meters, e.g. 1.5"; return; }
        var o = Options() with { IfExists = _s.Library.Settings.ExportIfExists switch { ExportCollisionSetting.Replace => ExistingFilePolicy.Overwrite, ExportCollisionSetting.Skip => ExistingFilePolicy.Skip, _ => ExistingFilePolicy.Rename } };
        _s.PanelMemory.Animation = o;
        LastOptions = o;
        var ids = _items.Select(i => i.Id).ToList();
        var lib = _s.Library; var before = _beforeQueue;
        string description = $"{Selected.Name} · {Size.Value} px · {Format.Label.Split(' ')[0]}";
        _ = Task.Run(async () =>
        {
            if (before != null) await before().ConfigureAwait(false);
            return lib.Animate(ids, o).ToList();
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!t.IsCompletedSuccessfully) { _notify("The animation could not be queued. " + (t.Exception?.GetBaseException() is { } x ? Text.Plain(x) : "")); return; }
                foreach (var id in t.Result) _queue.Describe(id, description);
                if (t.Result.Count < ids.Count) _notify($"{ids.Count - t.Result.Count} photo(s) cannot make this effect and were skipped");
            });
        }, TaskScheduler.Default);
        _notify(_items.Count == 1 ? "Animation added to the queue" : $"{Text.Number(_items.Count)} animations added to the queue");
        _close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _estimateCts.Cancel();
        if (Player.Preview != null) Player.Preview.Changed -= OnPreviewChanged;
        Player.Dispose();
        var opening = _opening;
        _ = Task.Run(async () => { if (opening != null && await opening.ConfigureAwait(false) is { } s) s.Dispose(); });
    }
}

/// <summary>The Pick buttons' "on" state.</summary>
public static class PickConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter IsFrom = new Avalonia.Data.Converters.FuncValueConverter<PickTarget, bool>(p => p == PickTarget.FocusFrom);
    public static readonly Avalonia.Data.Converters.IValueConverter IsTo = new Avalonia.Data.Converters.FuncValueConverter<PickTarget, bool>(p => p == PickTarget.FocusTo);
    public static readonly Avalonia.Data.Converters.IValueConverter IsFocus = new Avalonia.Data.Converters.FuncValueConverter<PickTarget, bool>(p => p == PickTarget.Focus);
}
