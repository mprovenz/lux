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
public enum PickTarget { None, FocusFrom, FocusTo, Focus }

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
        Dithers = new ChoiceOption[] { new("Fine pattern (steady)", (int)GifDither.Ordered), new("Diffusion (smoothest still)", (int)GifDither.Diffusion), new("None (smallest)", (int)GifDither.None) };
        // ---- the last settings of this run, else defaults
        var m = s.PanelMemory.Animation;
        _size = Sizes.FirstOrDefault(x => x.Value == (m?.LongEdge ?? 1040)) ?? Sizes[1];
        _format = Formats.FirstOrDefault(x => x.Value == (int)(m?.Format ?? AnimationFormat.Gif)) ?? Formats[0];
        _repeat = Repeats.FirstOrDefault(x => x.Value == (m?.Repeat ?? 0)) ?? Repeats[0];
        _path = Paths.FirstOrDefault(x => x.Value == (int)(m?.Path ?? AnimationPath.Sweep)) ?? Paths[0];
        _dither = Dithers.FirstOrDefault(x => x.Value == (int)(m?.GifDither ?? GifDither.Ordered)) ?? Dithers[0];
        _fNumber = m?.FNumber ?? 2.0;
        _frames = m?.Frames ?? 24;
        _amplitude = m?.AmplitudeMm ?? 71.49;
        _folder = m?.OutputFolder ?? s.PanelMemory.Export?.OutputFolder ?? s.Library.Settings.DefaultExportFolder ?? ExportPanelViewModel.DefaultFolderDisplay;
        var start = Effects.FirstOrDefault(x => x.Effect == (m?.Effect ?? AnimationEffect.RackFocus) && x.IsAvailable) ?? Effects.FirstOrDefault(x => x.IsAvailable) ?? Effects[0];
        _selected = start;
        start.IsSelected = true;
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

    /// <summary>Say plainly when the frames on screen have less detail than the file will have: while sharper frames replace them, or when
    /// the preview stays a quick one (the lens effects; a file larger than the sharper frames).</summary>
    void UpdateDetailNote()
    {
        string? text = null;
        if (Player.Preview is { IsPlanned: true } p && _fileSize is { } file && (p.Frame(0) ?? p.NearestFrame(0)) is { } f)
        {
            if (p.IsSharpening)
                text = $"Sharper frames replace the quick ones as they are made. The file is made at {file.W} × {file.H}.";
            else if (Math.Max(f.Width, f.Height) * 10 < Math.Max(file.W, file.H) * 9)
                text = $"The preview's frames are {f.Width} × {f.Height}: they show less detail than the file, which is made at {file.W} × {file.H}.";
        }
        if (PreviewDetailNote != text) PreviewDetailNote = text;
    }

    public bool IsRack => Selected.Effect == AnimationEffect.RackFocus;
    public bool IsAperture => Selected.Effect == AnimationEffect.ApertureSweep;
    public bool IsDepthEffect => IsRack || IsAperture;
    public bool IsParallax => Selected.Effect == AnimationEffect.ParallaxSweep;
    public bool HasFrameCount => IsDepthEffect || IsParallax;
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

    partial void OnSelectedChanged(EffectChoiceViewModel value) { if (_loading) return; Pick = PickTarget.None; RaiseEffect(); Changed(); }
    partial void OnSizeChanged(ChoiceOption value) => Changed(previewToo: false);
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
        OnPropertyChanged(nameof(IsPickingFrom)); OnPropertyChanged(nameof(IsPickingTo)); OnPropertyChanged(nameof(IsPickingFocus));
        Player.IsPicking = value != PickTarget.None;
        Player.PickHint = PickHint;
        OnPropertyChanged(nameof(PickHint));
    }

    void RaiseEffect()
    {
        foreach (var n in new[] { nameof(IsRack), nameof(IsAperture), nameof(IsDepthEffect), nameof(IsParallax), nameof(HasFrameCount), nameof(ApertureLabel),
                                  nameof(EffectNote), nameof(CanAdd) })
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
        Effect = Selected.Effect, Frames = Frames, PingPong = PingPong ? null : false, Repeat = Repeat.Value, LongEdge = Size.Value,
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
            var p = src.Preview(o);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || seq != _seq) return;
                if (Player.Preview != null) Player.Preview.Changed -= OnPreviewChanged;
                Player.Bind(p);
                p.Changed += OnPreviewChanged;
                PreviewNote = NotesOf(p);
                UpdateDetailNote();
            });
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
        // a finer estimate as frames arrive (every few frames, and at the end)
        if (sender is IAnimationPreview p && (p.IsComplete || Interlocked.Increment(ref _estimateTick) % 6 == 0)) _ = RefreshEstimateAsync();
        if (sender is IAnimationPreview { IsComplete: true }) RefreshEffects();
    }

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

    readonly Dictionary<PickTarget, (double X, double Y)> _picked = new();

    void OnPicked(double x, double y)
    {
        var target = Pick;
        if (target == PickTarget.None) return;
        Pick = PickTarget.None;
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
