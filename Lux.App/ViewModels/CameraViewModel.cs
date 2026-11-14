using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>Which captures the Camera page shows.</summary>
public enum CameraFilter { New, All, Since }

/// <summary>One capture on the camera (a cell of the Camera page).</summary>
public sealed partial class CameraItemViewModel : ObservableObject
{
    public CameraItemViewModel(CameraCapture c, bool showAsHeld = false) { Capture = c; _showAsHeld = showAsHeld; }

    public CameraCapture Capture { get; private set; }
    bool _showAsHeld;
    /// <summary>The degrees by which the page turns the thumbnail. It is the way the camera was held, when the setting "show photos as held" is
    /// on. Otherwise it is 0.</summary>
    public int Rotation => _showAsHeld && Capture.HeldDegrees is 90 or 180 or 270 ? Capture.HeldDegrees.Value : 0;
    public bool ShowAsHeld
    {
        get => _showAsHeld;
        set { if (_showAsHeld == value) return; int before = Rotation; _showAsHeld = value; if (Rotation != before) OnPropertyChanged(nameof(Rotation)); }
    }
    public string Key => Capture.Key;
    public string Name => Capture.Name;
    public string SizeText => Text.Mb(Capture.Size);
    public bool InLibrary => Capture.InLibrary;
    public bool HasPreview => Capture.PreviewFile != null;
    public string AccessibleName => $"{Name}, {SizeText}" + (InLibrary ? ", in the library" : ", new") + (IsSelected ? ", selected" : "");
    public string Tip => $"{Capture.FileName} · {Text.Mb(Capture.Size)} · {(Capture.Modified is { } m ? m.ToLocalTime().ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) : "no date")}"
                         + (InLibrary ? " · already in the library" : "") + (HasPreview ? "" : " · the camera has no preview of it");

    [ObservableProperty] bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(AccessibleName));

    /// <summary>The cell's command (a click or the tick box): toggles it in the page's selection.</summary>
    public IRelayCommand? ToggleCommand { get; set; }

    public void Update(CameraCapture c)
    {
        bool preview = c.PreviewFile != Capture.PreviewFile;
        int rotation = Rotation;
        Capture = c;
        if (Rotation != rotation) OnPropertyChanged(nameof(Rotation));
        OnPropertyChanged(nameof(Capture)); OnPropertyChanged(nameof(InLibrary)); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(Tip));
        if (preview) OnPropertyChanged(nameof(HasPreview));
    }
}

/// <summary>A day heading of the Camera page ("12 October 2026 · 16 new") with its "Select day" / "Clear day" action.</summary>
public sealed class CameraDayHeaderViewModel(string title, string sub, IReadOnlyList<CameraItemViewModel> members, IRelayCommand selectDay) : ObservableObject
{
    public string Title { get; } = title;
    public string Sub { get; } = sub;
    public IReadOnlyList<CameraItemViewModel> Members { get; } = members;
    public IRelayCommand SelectDayCommand { get; } = selectDay;
    public string SelectLabel => Members.All(m => m.IsSelected) ? "Clear day" : "Select day";
    public void Refresh() => OnPropertyChanged(nameof(SelectLabel));
}

/// <summary>A row of camera cells of one day.</summary>
public sealed class CameraRowViewModel(IReadOnlyList<CameraItemViewModel> cells, CellMetrics metrics)
{
    public IReadOnlyList<CameraItemViewModel> Cells { get; } = cells;
    public CellMetrics Metrics { get; } = metrics;
}

/// <summary>A day to show captures since (the "Since a date…" choice).</summary>
public sealed record CameraDayChoice(DateOnly Day, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Camera page (S-Camera): the camera's state, the New / All / Since a date filter, the captures grouped by day with tick boxes, the import
/// panel (destination, one folder per day, what happens after the copy, the Import button), the last import, and the page's status-bar texts.
/// The camera is read-only: the page lists, previews and copies; nothing on the camera is changed.
/// </summary>
public sealed partial class CameraViewModel : ObservableObject
{
    public const double MinCell = 120, Gap = 8, Padding = 12;

    readonly AppServices _s;
    readonly ICameraService _cam;
    readonly Action<string> _notify;
    readonly Dictionary<string, CameraItemViewModel> _items = new(StringComparer.Ordinal);
    readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    List<CameraItemViewModel> _visible = new();
    CameraStatus _status;
    bool _userChoseSelection, _refreshPosted, _planPosted, _showAsHeld;
    string? _lastCamera;
    bool _wasOpen, _rowsDirty = true;
    double _viewportWidth = -1;
    CancellationTokenSource? _planCts;
    JobInfo? _importJob;

    public CameraViewModel(AppServices s, LibraryEvents events, Action<string> notify)
    {
        _s = s;
        _cam = s.Camera;
        _notify = notify;
        _status = _cam.Status;
        var settings = s.Library.Settings;
        _destination = settings.GetValue("camera.destination") is { Length: > 0 } d ? d : _cam.DefaultDestination;
        _oneFolderPerDay = settings.GetValue("camera.per_day") != "false";
        _afterIndex = int.TryParse(settings.GetValue("camera.after"), out int a) && a is >= 0 and <= 2 ? a : 0;
        Thumbnails = new ThumbnailCache(LoadPreviewAsync);
        Controls.CameraThumbnailView.Cache = Thumbnails;
        _showAsHeld = settings.ShowPhotosAsHeld;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ILibrarySettings.ShowPhotosAsHeld))
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _showAsHeld = _s.Library.Settings.ShowPhotosAsHeld;
                    foreach (var i in _items.Values) i.ShowAsHeld = _showAsHeld;
                });
        };
        _cam.StatusChanged += (_, _) => Post();
        _cam.CapturesChanged += (_, _) => Post();
        _cam.LastImportChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RaiseLastImport);
        events.JobsChanged += OnJobs;
        events.CapturesChanged += (_, _) => RaiseLastImport();   // the analysis count of the last import
        SetViewportWidth(1100);   // until the page reports its width
        Refresh();
    }

    /// <summary>Decoded previews of the camera's captures (only realised cells ask).</summary>
    public ThumbnailCache Thumbnails { get; }
    public CellMetrics Metrics { get; } = new();

    async Task<byte[]?> LoadPreviewAsync(string key, CancellationToken cancel)
    {
        if (!_items.TryGetValue(key, out var vm)) return null;
        return await _cam.GetPreviewAsync(vm.Capture, cancel).ConfigureAwait(false);
    }

    void Post()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _refreshPosted = false; Refresh(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Read the camera's state and captures again (events call it; tests too).</summary>
    public void Refresh()
    {
        _status = _cam.Status;
        var caps = _cam.Captures;
        string? camera = _status.Serial ?? _status.DeviceName;
        if (camera != _lastCamera && camera != null) { _lastCamera = camera; _userChoseSelection = false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool layout = _status.IsOpen != _wasOpen;   // only a change of what shows, or where it shows, rebuilds the rows; a held turn does not
        _wasOpen = _status.IsOpen;
        foreach (var c in caps)
        {
            seen.Add(c.Key);
            if (_items.TryGetValue(c.Key, out var vm))
            {
                if (Equals(vm.Capture, c)) continue;
                var was = vm.Capture;
                bool wasNew = !vm.InLibrary;
                if (was.Size != c.Size || was.PreviewFile != c.PreviewFile) Thumbnails.Invalidate(c.Key);   // the file changed on the camera, so it needs a new thumbnail
                if (was.InLibrary != c.InLibrary || was.Day != c.Day || was.Modified != c.Modified || was.Size != c.Size) layout = true;
                vm.Update(c);
                if (wasNew && c.InLibrary) _selected.Remove(c.Key);   // imported: it leaves the selection
            }
            else
            {
                vm = new CameraItemViewModel(c, _showAsHeld);
                vm.ToggleCommand = new RelayCommand(() => Toggle(vm, false));
                _items[c.Key] = vm;
                layout = true;
                // nothing starts selected (a camera can hold tens of GB of new captures)
            }
        }
        // while the camera is away its captures and the selection are kept (hidden): the same camera plugged in again shows them as they were
        if (_status.IsOpen)
            foreach (var gone in _items.Keys.Where(k => !seen.Contains(k)).ToList()) { _items.Remove(gone); _selected.Remove(gone); layout = true; }
        if (layout || _rowsDirty)
        {
            _rowsDirty = false;
            UpdateSinceChoices();
            Rebuild();
        }
        RaiseState();
    }

    // ============================================================================================================ the state strip

    public CameraStatus Status => _status;
    public CameraState State => _status.State;
    public bool IsOpen => _status.IsOpen;

    public string StateTitle => _status.State switch
    {
        CameraState.Unavailable => "Camera import is not available",
        CameraState.NotConnected => "No camera connected",
        CameraState.Connecting => "Opening the camera…",
        CameraState.Busy => "The camera is in use by another program",
        CameraState.Error => "The camera could not be read",
        _ => $"{_status.DeviceName ?? "Camera"} · connected over USB",
    };

    public string StateLine
    {
        get
        {
            var s = _status;
            string serial = s.Serial is { } sn ? sn + " · " : "";
            switch (s.State)
            {
                // the full explanation is in the card below
                case CameraState.Unavailable: return "";
                case CameraState.Busy: return "Lux tries again every few seconds.";
                case CameraState.Error: return "Lux tries again in a moment.";
                case CameraState.NotConnected: return "Lux looks for the camera every few seconds.";
                case CameraState.Connecting: return "";
                case CameraState.Listing when s.CheckingEarlierList && s.Phase == CameraListingPhase.FileList:
                {
                    double el = s.PhaseStartedUtc is { } t ? Math.Max(0, (DateTime.UtcNow - t).TotalSeconds) : 0;
                    string when = s.EarlierListUtc is { } w ? $" from {Text.ShortDateTime(w.ToLocalTime())}" : "";
                    string exp = s.ExpectedFileListSeconds is double e && e >= 1 ? $" (about {Math.Round(e)} s)" : "";
                    return $"{serial}showing the list{when} · checking the camera for changes · {Math.Round(el)} s{exp}";
                }
                case CameraState.Listing when s.CheckingEarlierList:
                    return $"{serial}checking the camera for changes · {Text.Number(s.Listed)} of {Text.Number(s.ListTotal)}" + (s.New > 0 ? $" · {Text.Number(s.New)} not yet imported" : "");
                case CameraState.Listing when s.Phase == CameraListingPhase.FileList:
                {
                    double el = s.PhaseStartedUtc is { } t ? Math.Max(0, (DateTime.UtcNow - t).TotalSeconds) : 0;
                    string exp = s.ExpectedFileListSeconds is double e ? $" (about {Math.Round(e)} s; the camera sends it all at once)" : "";
                    return $"{serial}reading the camera's list of files · {Math.Round(el)} s{exp}";
                }
                case CameraState.Listing:
                    return $"{serial}reading photo details · {Text.Number(s.Listed)} of {Text.Number(s.ListTotal)}" + (s.New > 0 ? $" · {Text.Number(s.New)} not yet imported" : "");
                default:
                    var parts = new List<string> { Text.Count(s.Captures, "capture on the camera", "captures on the camera") };
                    parts.Add(s.New > 0 ? $"{Text.Number(s.New)} not yet imported" : "all in your library");
                    if (s.BatteryPercent is int b) parts.Add($"battery {b} %");
                    if (s.NotLightL16) parts.Add("not a Light L16");
                    return serial + string.Join(" · ", parts);
            }
        }
    }

    /// <summary>The strip's progress bar (not while the page's own waiting card shows one).</summary>
    public bool IsListing => (_status.State == CameraState.Listing || _status.State == CameraState.Connecting) && !ShowWaiting;
    public bool ListingIndeterminate => _status.State == CameraState.Connecting || _status.Phase == CameraListingPhase.FileList;
    public double ListingProgress => _status.ListTotal > 0 ? _status.Listed / (double)_status.ListTotal : 0;
    public bool CanRetry => _status.State is CameraState.Busy or CameraState.Error;
    public bool CanReread => _status.State == CameraState.Connected;
    public bool IsWarning => _status.State is CameraState.Busy or CameraState.Error;

    [RelayCommand] void Retry() => _cam.Reconnect();

    // ============================================================================================================ the filter

    [ObservableProperty] CameraFilter _filter = CameraFilter.New;
    partial void OnFilterChanged(CameraFilter value) { Rebuild(); RaiseState(); }

    [RelayCommand] void SetFilter(CameraFilter f) => Filter = f;

    public bool IsNewFilter => Filter == CameraFilter.New;
    public bool IsAllFilter => Filter == CameraFilter.All;
    public bool IsSinceFilter => Filter == CameraFilter.Since;
    public int NewCount => _items.Values.Count(i => !i.InLibrary);
    public string NewLabel => IsOpen ? $"New · {Text.Number(NewCount)}" : "New";
    public string AllLabel => IsOpen ? $"All · {Text.Number(_items.Count)}" : "All";
    public string SinceLabel => Filter == CameraFilter.Since && SinceDay is { } d ? $"Since {d.Day.ToString("d MMM", CultureInfo.InvariantCulture)} · {Text.Number(_visible.Count)}" : "Since a date…";

    [ObservableProperty] IReadOnlyList<CameraDayChoice> _sinceChoices = Array.Empty<CameraDayChoice>();
    [ObservableProperty] CameraDayChoice? _sinceDay;
    bool _settingSince, _sinceChosen;
    partial void OnSinceDayChanged(CameraDayChoice? value)
    {
        if (!_settingSince) _sinceChosen = true;   // the user's choice is kept while the list changes
        if (Filter == CameraFilter.Since) { Rebuild(); RaiseState(); }
    }

    void UpdateSinceChoices()
    {
        var days = _items.Values.Where(i => i.Capture.Day is not null).GroupBy(i => i.Capture.Day!.Value).OrderByDescending(g => g.Key)
                         .Select(g => new CameraDayChoice(g.Key, $"{Text.Day(g.Key)} · {Text.Count(g.Count(), "capture", "captures")}")).ToList();
        if (days.Select(d => d.Label).SequenceEqual(SinceChoices.Select(d => d.Label))) return;
        var keep = _sinceChosen ? SinceDay?.Day : null;
        _settingSince = true;
        try
        {
            SinceChoices = days;
            // the day the user chose, else the day of the last import, else a week back (the oldest capture day on or after it)
            DateOnly? want = keep;
            if (want is null && _cam.LastImport is { } li) want = DateOnly.FromDateTime(li.StartedUtc.ToLocalTime());
            want ??= DateOnly.FromDateTime(DateTime.Now.AddDays(-7));
            SinceDay = days.Where(d => d.Day >= want.Value).LastOrDefault() ?? days.FirstOrDefault();
        }
        finally { _settingSince = false; }
    }

    bool Shows(CameraItemViewModel i) => Filter switch
    {
        CameraFilter.New => !i.InLibrary,
        CameraFilter.Since => SinceDay is { } d ? i.Capture.Day is { } day && day >= d.Day : true,
        _ => true,
    };

    // ============================================================================================================ the captures

    [ObservableProperty] IReadOnlyList<object> _rows = Array.Empty<object>();

    /// <summary>The captures shown, in display order (days newest first, within a day by time).</summary>
    public IReadOnlyList<CameraItemViewModel> Visible => _visible;

    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 0.5) return;
        _viewportWidth = width;
        int cols = Math.Max(1, (int)Math.Floor((width - 2 * Padding + Gap) / (MinCell + Gap)));
        double w = Math.Floor((width - 2 * Padding - Gap * (cols - 1)) / cols);
        bool rebuild = cols != Metrics.Columns;
        Metrics.Columns = cols; Metrics.CellWidth = w; Metrics.ImageHeight = Math.Round((w - 10) * 0.75);
        if (rebuild) Rebuild();
    }

    void Rebuild()
    {
        _visible = _items.Values.Where(Shows)
            .OrderByDescending(i => i.Capture.Day ?? DateOnly.MinValue)
            .ThenBy(i => i.Capture.Modified ?? DateTimeOffset.MaxValue).ThenBy(i => i.Capture.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var i in _items.Values) i.IsSelected = _selected.Contains(i.Key);
        int n = Math.Max(1, Metrics.Columns);
        var rows = new List<object>();
        for (int i = 0; i < _visible.Count;)
        {
            var day = _visible[i].Capture.Day;
            int j = i;
            while (j < _visible.Count && _visible[j].Capture.Day == day) j++;
            var members = _visible.GetRange(i, j - i);
            int fresh = members.Count(m => !m.InLibrary);
            int onCamera = _items.Values.Count(m => m.Capture.Day == day);
            string sub = Filter == CameraFilter.New
                ? (onCamera > fresh ? $"{Text.Number(fresh)} new of {Text.Number(onCamera)}" : $"{Text.Number(fresh)} new")
                : $"{Text.Count(members.Count, "capture", "captures")} · " + (fresh == 0 ? "all in the library" : $"{Text.Number(fresh)} new");
            var dayMembers = members;
            rows.Add(new CameraDayHeaderViewModel(Text.Day(day), sub, dayMembers, new RelayCommand(() => SelectDay(dayMembers))));
            for (int k = 0; k < members.Count; k += n) rows.Add(new CameraRowViewModel(members.GetRange(k, Math.Min(n, members.Count - k)), Metrics));
            i = j;
        }
        Rows = rows;
        RaiseSelection();
    }

    // ============================================================================================================ selection

    int _anchor = -1;

    /// <summary>A click on a cell or its tick box: toggles it; with Shift, everything from the last click to here takes the clicked one's new state.</summary>
    public void Toggle(CameraItemViewModel item, bool shift)
    {
        _userChoseSelection = true;
        int idx = _visible.IndexOf(item);
        bool on = !_selected.Contains(item.Key);
        if (shift && _anchor >= 0 && idx >= 0)
        {
            int a = Math.Min(_anchor, idx), b = Math.Max(_anchor, idx);
            for (int k = a; k <= b; k++) { if (on) _selected.Add(_visible[k].Key); else _selected.Remove(_visible[k].Key); _visible[k].IsSelected = on; }
        }
        else
        {
            if (on) _selected.Add(item.Key); else _selected.Remove(item.Key);
            item.IsSelected = on;
        }
        _anchor = idx;
        foreach (var h in Rows.OfType<CameraDayHeaderViewModel>()) h.Refresh();
        RaiseSelection();
    }

    void SelectDay(IReadOnlyList<CameraItemViewModel> members)
    {
        _userChoseSelection = true;
        bool allOn = members.All(m => _selected.Contains(m.Key));
        foreach (var m in members) { if (allOn) _selected.Remove(m.Key); else _selected.Add(m.Key); }
        Rebuild();
    }

    public bool AllVisibleSelected => _visible.Count > 0 && _visible.All(v => _selected.Contains(v.Key));
    public string SelectAllLabel => AllVisibleSelected ? "Select none" : "Select all";

    [RelayCommand]
    void SelectAll()
    {
        _userChoseSelection = true;
        bool none = AllVisibleSelected;
        foreach (var v in _visible) { if (none) _selected.Remove(v.Key); else _selected.Add(v.Key); }
        Rebuild();
    }

    /// <summary>The selected captures the filter shows (what Import copies).</summary>
    public IReadOnlyList<CameraCapture> SelectedCaptures => _visible.Where(v => _selected.Contains(v.Key)).Select(v => v.Capture).ToList();
    public int SelectedCount => _visible.Count(v => _selected.Contains(v.Key));
    public long SelectedBytes => _visible.Where(v => _selected.Contains(v.Key)).Sum(v => Math.Max(0, v.Capture.Size));

    void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedBytes)); OnPropertyChanged(nameof(SelectAllLabel));
        OnPropertyChanged(nameof(AllVisibleSelected)); OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(ImportButtonText)); OnPropertyChanged(nameof(Visible)); OnPropertyChanged(nameof(SinceLabel));
        OnPropertyChanged(nameof(ShowGrid)); OnPropertyChanged(nameof(ShowNothingNew)); OnPropertyChanged(nameof(ShowNoCaptures));
        SchedulePlan();
    }

    // ============================================================================================================ the import panel

    [ObservableProperty] string _destination;
    partial void OnDestinationChanged(string value) { _s.Library.Settings.SetValue("camera.destination", value); SchedulePlan(); }

    [ObservableProperty] bool _oneFolderPerDay;
    partial void OnOneFolderPerDayChanged(bool value) { _s.Library.Settings.SetValue("camera.per_day", value ? "true" : "false"); SchedulePlan(); }

    public static IReadOnlyList<string> AfterChoices { get; } = new[] { "Analyse and keep a 13 MP render", "Analyse and keep full detail", "Only copy" };

    [ObservableProperty] int _afterIndex;
    partial void OnAfterIndexChanged(int value)
    {
        _s.Library.Settings.SetValue("camera.after", value.ToString(CultureInfo.InvariantCulture));
        OnPropertyChanged(nameof(AfterHint));
    }

    public CameraAfterImport After => AfterIndex switch { 1 => CameraAfterImport.AnalyseFullDetail, 2 => CameraAfterImport.CopyOnly, _ => CameraAfterImport.Analyse13Mp };

    public string AfterHint => After switch
    {
        CameraAfterImport.AnalyseFullDetail => "Analysing takes about a minute per capture; full detail keeps about 270 MB per capture so that every zoom is sharp at once. Captures stay on the camera; nothing is deleted there.",
        CameraAfterImport.CopyOnly => "The captures are added to the library without analysis; you can analyse them later. Captures stay on the camera; nothing is deleted there.",
        _ => "Analysing takes about a minute per capture and makes later editing immediate. Captures stay on the camera; nothing is deleted there.",
    };

    [RelayCommand]
    async Task ChooseFolder()
    {
        var path = await _s.Platform.PickFolderAsync("Choose where to copy the captures").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path)) Destination = path;
    }

    [ObservableProperty] CameraImportPlan? _plan;
    partial void OnPlanChanged(CameraImportPlan? value)
    {
        OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(Problem)); OnPropertyChanged(nameof(HasProblem)); OnPropertyChanged(nameof(PlanNote));
        OnPropertyChanged(nameof(FreeSpaceText)); OnPropertyChanged(nameof(FolderPreview));
    }

    CameraImportOptions Options => new(Destination?.Trim() ?? "", OneFolderPerDay, After);

    void SchedulePlan()
    {
        if (_planPosted) return;
        _planPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _planPosted = false; _ = UpdatePlanAsync(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    Task _planTask = Task.CompletedTask;

    /// <summary>Work out what Import would do now (file checks off the UI thread; the newest request wins). Completes when the newest plan
    /// is shown (tests await it).</summary>
    public Task UpdatePlanAsync()
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        var t = ComputePlanAsync(cts);
        _planTask = t;
        return LatestAsync(t);
    }

    async Task LatestAsync(Task t)
    {
        await t.ConfigureAwait(true);
        while (!ReferenceEquals(t, _planTask)) { t = _planTask; await t.ConfigureAwait(true); }
    }

    async Task ComputePlanAsync(CancellationTokenSource cts)
    {
        var caps = SelectedCaptures;
        var options = Options;
        CameraImportPlan plan;
        try { plan = await Task.Run(() => _cam.PlanImport(caps, options), cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { plan = new CameraImportPlan { Count = caps.Count, Problem = e.Message }; }
        if (!cts.IsCancellationRequested) Plan = plan;
    }

    public string ImportTitle => SelectedCount == 0 ? "Nothing selected" : $"Import {Text.Number(SelectedCount)} selected · {Text.Mb(SelectedBytes)}";
    public string ImportButtonText => IsImporting ? "Importing…" : SelectedCount == 0 ? "Import" : SelectedCount == 1 ? "Import 1 capture" : $"Import {Text.Number(SelectedCount)} captures";
    public bool CanImport => !IsImporting && Plan is { CanImport: true } p && p.Count == SelectedCount && SelectedCount > 0;
    public string? Problem => SelectedCount == 0 ? null : Plan?.Problem;
    public bool HasProblem => Problem != null;

    /// <summary>"2 are already in the folder and are skipped".</summary>
    public string PlanNote => Plan is { AlreadyThere: > 0 } p && p.Problem == null
        ? (p.AlreadyThere == 1 ? "1 is already in the folder and is skipped." : $"{Text.Number(p.AlreadyThere)} are already in the folder and are skipped.") : "";

    /// <summary>"→ …/2026-10-12 and 1 more day folder".</summary>
    public string FolderPreview
    {
        get
        {
            if (Plan is not { Folders.Count: > 0 } p || !OneFolderPerDay) return "";
            string first = Path.GetFileName(p.Folders[^1].TrimEnd('/', '\\'));
            return p.Folders.Count == 1 ? $"Into the folder {first}" : $"Into {Text.Number(p.Folders.Count)} day folders, {Path.GetFileName(p.Folders[0])} to {first}";
        }
    }

    public string FreeSpaceText => Plan?.FreeBytes is long f ? $"Free space in the destination: {Text.Mb(f)}" : "";   // decimal, as the sizes beside it

    [RelayCommand]
    void Import()
    {
        if (!CanImport) return;
        var caps = SelectedCaptures;
        try
        {
            long id = _cam.StartImport(caps, Options);
            _importJob = _s.Library.Jobs.Get(id);
            _notify(caps.Count == 1 ? $"Importing {caps[0].Name} — it is in the Queue" : $"Importing {caps.Count} captures — they are in the Queue");
        }
        catch (LibraryException e) { _notify(e.Message); }
        RaiseImport();
    }

    // ============================================================================================================ the import running and the last one

    void OnJobs(IReadOnlyCollection<JobChangedEventArgs> changes)
    {
        bool any = false;
        foreach (var c in changes)
        {
            if (c.Job.Kind != JobKind.Import) continue;
            any = true;
            if (c.Removed) { if (_importJob?.Id == c.Job.Id) _importJob = null; continue; }
            if (!c.Job.IsFinished) _importJob = c.Job;
            else if (_importJob?.Id == c.Job.Id) _importJob = null;
        }
        if (any) { RaiseImport(); RaiseLastImport(); }
    }

    public bool IsImporting => _importJob is { IsFinished: false } || _status.IsImporting;
    public double ImportProgress => _importJob?.Progress ?? 0;
    public string ImportProgressText
    {
        get
        {
            if (_importJob is not { } j) return _status.IsImporting ? "Importing" : "";
            if (j.State is JobState.Waiting or JobState.Paused) return j.State == JobState.Paused ? "Import paused in the Queue" : "Import waiting in the Queue";
            string items = j.ItemsTotal is int t ? $" · {Math.Min(t, (j.ItemsDone ?? 0) + 1)} of {t}" : "";
            return $"{(j.Status.Length > 0 ? j.Status : "Importing")}{items} · {Text.Percent(j.Progress)}";
        }
    }

    [RelayCommand]
    void CancelImport()
    {
        if (_importJob is { } j) { long id = j.Id; _ = Task.Run(() => _s.Library.Jobs.Cancel(id)); }
    }

    void RaiseImport()
    {
        OnPropertyChanged(nameof(IsImporting)); OnPropertyChanged(nameof(ImportProgress)); OnPropertyChanged(nameof(ImportProgressText));
        OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(ImportButtonText));
    }

    public CameraImportSummary? LastImport => _cam.LastImport;
    public bool HasLastImport => LastImport != null;

    public string LastImportWhen => LastImport is { } l
        ? $"{l.StartedUtc.ToLocalTime().ToString("d MMMM", CultureInfo.InvariantCulture)}, {Text.Count(l.Requested, "capture", "captures")}" : "";

    public string LastImportState => LastImport?.State switch
    {
        CameraImportState.Running => "Copying",
        CameraImportState.Complete => "Complete",
        CameraImportState.Cancelled => "Cancelled",
        CameraImportState.Stopped => "Stopped",
        _ => "",
    };

    public bool LastImportFailed => LastImport?.State is CameraImportState.Stopped or CameraImportState.Cancelled;

    public string LastImportCopied => LastImport is { } l
        ? $"{Text.Number(l.Copied + l.Skipped)} of {Text.Number(l.Requested)}" + (l.Skipped > 0 ? $" ({Text.Number(l.Skipped)} already there)" : "") : "";

    /// <summary>The analysis line is a count ("3 of 12"), set in the number face; words are not.</summary>
    public bool LastImportAnalysisIsCount => LastImportAnalysis.Contains(" of ", StringComparison.Ordinal) && char.IsDigit(LastImportAnalysis[0]);
    public string LastImportAnalysisLabel => LastImport?.After == CameraAfterImport.CopyOnly ? "Analysis" : "Analysed";
    public string LastImportAnalysis
    {
        get
        {
            var l = LastImport;
            if (l is null) return "";
            if (l.After == CameraAfterImport.CopyOnly) return "not asked for";
            if (l.State != CameraImportState.Complete) return "after the copy";
            return _cam.LastImportAnalysis() is { } a ? $"{Text.Number(a.Analysed)} of {Text.Number(a.Total)}" : "waiting for the folder scan";
        }
    }

    public string? LastImportProblem => LastImport?.Problem;
    public bool HasLastImportProblem => LastImportProblem != null;
    /// <summary>A stopped or cancelled import can be continued (its job is retried: the files already copied are skipped).</summary>
    public bool CanContinue => LastImportFailed && LastImport?.JobId is long id && _s.Library.Jobs.Get(id) is { IsFinished: true } && IsOpen && !IsImporting;

    [RelayCommand]
    void ContinueImport()
    {
        if (LastImport?.JobId is not long id) return;
        _ = Task.Run(() => _s.Library.Jobs.Retry(id)).ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _notify(t.IsCompletedSuccessfully && t.Result is not null ? "Continuing the import — the photos already copied are skipped" : "The import cannot be continued; import the photos again")),
            TaskScheduler.Default);
    }

    void RaiseLastImport()
    {
        OnPropertyChanged(nameof(LastImport)); OnPropertyChanged(nameof(HasLastImport)); OnPropertyChanged(nameof(LastImportWhen)); OnPropertyChanged(nameof(LastImportState));
        OnPropertyChanged(nameof(LastImportFailed)); OnPropertyChanged(nameof(LastImportCopied)); OnPropertyChanged(nameof(LastImportAnalysis));
        OnPropertyChanged(nameof(LastImportAnalysisLabel)); OnPropertyChanged(nameof(LastImportAnalysisIsCount)); OnPropertyChanged(nameof(LastImportProblem)); OnPropertyChanged(nameof(HasLastImportProblem));
        OnPropertyChanged(nameof(CanContinue));
    }

    // ============================================================================================================ the main area and the status bar

    public bool ShowGrid => IsOpen && _visible.Count > 0;
    public bool ShowConnect => _status.State is CameraState.NotConnected or CameraState.Unavailable;
    public bool ShowProblemCard => _status.State is CameraState.Busy or CameraState.Error;
    public bool ShowWaiting => (_status.State == CameraState.Connecting || (_status.State == CameraState.Listing && _items.Count == 0));
    public bool ShowNothingNew => IsOpen && _status.State == CameraState.Connected && Filter == CameraFilter.New && _visible.Count == 0 && _items.Count > 0;
    public bool ShowNoCaptures => IsOpen && _status.State == CameraState.Connected && _items.Count == 0;
    public string ConnectTitle => _status.State == CameraState.Unavailable ? "Camera import is not available" : "Connect the camera";
    public string ConnectText => _status.State == CameraState.Unavailable ? _status.Message ?? ""
        : "Connect the Light L16 with its USB cable and switch it on. Lux finds it by itself and lists the captures on it, grouped by day, with the new ones selected. "
          + "Lux only reads from the camera: nothing on it is changed or deleted.";
    public string WaitingText => _status.State == CameraState.Connecting ? "Opening the camera…"
        : $"Reading the camera's list of files. The camera sends it all at once: about {Math.Round(_status.ExpectedFileListSeconds ?? 20)} seconds.";

    [RelayCommand] void ShowAll() => Filter = CameraFilter.All;

    /// <summary>The status bar's left text on this page: "14 of 38 new captures selected".</summary>
    public string SelectionSummary
    {
        get
        {
            if (!IsOpen || _items.Count == 0) return "";   // the camera's state is on the right of the status bar
            string what = Filter switch
            {
                CameraFilter.New => Text.Count(_visible.Count, "new capture", "new captures"),
                CameraFilter.Since when SinceDay is { } d => $"{Text.Count(_visible.Count, "capture", "captures")} since {d.Day.ToString("d MMMM", CultureInfo.InvariantCulture)}",
                _ => Text.Count(_visible.Count, "capture", "captures"),
            };
            return $"{Text.Number(SelectedCount)} of {what} selected";
        }
    }

    void RaiseState()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(IsOpen)); OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateLine)); OnPropertyChanged(nameof(IsListing)); OnPropertyChanged(nameof(ListingIndeterminate)); OnPropertyChanged(nameof(ListingProgress));
        OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanReread)); OnPropertyChanged(nameof(IsWarning)); OnPropertyChanged(nameof(IsNewFilter));
        OnPropertyChanged(nameof(IsAllFilter)); OnPropertyChanged(nameof(IsSinceFilter)); OnPropertyChanged(nameof(NewCount)); OnPropertyChanged(nameof(NewLabel));
        OnPropertyChanged(nameof(AllLabel)); OnPropertyChanged(nameof(SinceLabel)); OnPropertyChanged(nameof(ShowGrid)); OnPropertyChanged(nameof(ShowConnect));
        OnPropertyChanged(nameof(ShowProblemCard)); OnPropertyChanged(nameof(ShowWaiting)); OnPropertyChanged(nameof(ShowNothingNew)); OnPropertyChanged(nameof(ShowNoCaptures));
        OnPropertyChanged(nameof(ConnectTitle)); OnPropertyChanged(nameof(ConnectText)); OnPropertyChanged(nameof(WaitingText)); OnPropertyChanged(nameof(SelectionSummary));
        RaiseImport();
        RaiseLastImport();
        StatusTextsChanged?.Invoke();
    }

    /// <summary>The page's status-bar texts changed (the shell re-reads them).</summary>
    public event Action? StatusTextsChanged;

    /// <summary>The file-list phase shows its elapsed seconds: the page asks for a fresh line once a second while it lasts.</summary>
    public void Tick() { if (_status.Phase == CameraListingPhase.FileList) OnPropertyChanged(nameof(StateLine)); }
}
