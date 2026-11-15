using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>What the library page needs from the shell.</summary>
public interface IShell
{
    /// <summary>Choose a folder and add it to the library.</summary>
    Task AddFolderAsync();
    void OpenInDevelop(CaptureItemViewModel item);
    void ShowDialog(object dialog);
    void CloseDialog(object dialog);
    void Notify(string message);
    void OpenLumenImport(CaptureQuery scope, string label, IReadOnlyCollection<string>? only = null);
    void ShowSettings();
    void ShowLibrary();
    /// <summary>A notice with an action (e.g. "Undo") in the status bar.</summary>
    void Notify(string message, string actionLabel, Action action);
    /// <summary>Closes the app, installs the downloaded version and starts the app again. Does nothing when no version is ready.</summary>
    void RestartToUpdate();
}

/// <summary>
/// The library page: folder tree, filter, chips, sort, the three browse views (one ordered result, three row layouts), selection and
/// keyboard model, details panel and export side panel.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public const double GridGap = 8, GridPadding = 12, CardGap = 12, CardPadding = 16;
    public const double MinGridSize = 90, MaxGridSize = 320, MinCardSize = 120, MaxCardSize = 400;

    readonly AppServices _s;
    readonly ILibrary _lib;
    readonly QueueViewModel _queue;
    readonly IShell _shell;
    List<CaptureItemViewModel> _items = new();
    Dictionary<string, CaptureItemViewModel> _byId = new(StringComparer.Ordinal);
    readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    CancellationTokenSource? _loadCts, _filterCts, _treeCts;
    int[] _rowOfItem = Array.Empty<int>();
    int _cursor = -1, _anchor = -1;
    double _viewportWidth = 900;
    bool _requeryPending;

    public LibraryViewModel(AppServices s, LibraryEvents events, QueueViewModel queue, IShell shell)
    {
        _s = s;
        _lib = s.Library;
        _queue = queue;
        _shell = shell;
        QualityChanges = new QualityChanges(s, m => shell.Notify(m));
        Details = new DetailsViewModel(this, s);
        Interaction = new InteractionHint(s.Library.Jobs);
        Chips = new ObservableCollection<ChipViewModel>
        {
            new(null, "All") { IsOn = true },
            new(null, "Rated 4 and up", minRating: 4),
            new(SmartFilter.DepthEdited, "Depth edited"),
            new(SmartFilter.Stacked, "Low light stacks"),
            new(SmartFilter.Telephoto, "Telephoto"),
            new(SmartFilter.NotAnalysed, "Not analysed"),
        };
        _gridSize = ReadDouble("browse.gridSize", 120);
        _cardSize = ReadDouble("browse.cardSize", 170);
        s.Develop.PresetsChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPresets);
        RefreshPresets();
        s.Develop.ClipboardChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(CanPasteAdjustments)); OnPropertyChanged(nameof(PasteLabel)); });
        events.CapturesChanged += OnCapturesChanged;
        events.FoldersChanged += () => _ = RefreshTreeAsync();
        events.AlbumsChanged += () => _ = RefreshTreeAsync();
        queue.ActiveCapturesChanged += ids =>
        {
            // The library reports the capture's new state a moment after its job ends. The captures whose job ended are read again at once,
            // so that no view shows a change under way after the job, or the quality kept before it.
            var ended = ids.Where(id => queue.ActiveJobOf(id) == null && _byId.ContainsKey(id)).ToList();
            if (ended.Count > 0) OnCapturesChanged(ended, false);
            foreach (var id in ids) if (_byId.TryGetValue(id, out var vm)) vm.JobStateChanged();
            if (Details.Current is { } cur && ids.Contains(cur.Id)) Details.JobProgress();
        };
    }

    public DetailsViewModel Details { get; }
    /// <summary>Raising / lowering the kept quality with its notice — shared by the selection bar, the context menu and the details panel.</summary>
    public QualityChanges QualityChanges { get; }
    /// <summary>Pointer / keyboard / scroll activity on the library page tells the queue the user is working.</summary>
    public InteractionHint Interaction { get; }
    public ObservableCollection<TreeNodeViewModel> Tree { get; } = new();
    public ObservableCollection<ChipViewModel> Chips { get; }
    public CellMetrics GridMetrics { get; } = new();
    public CellMetrics CardMetrics { get; } = new();
    public IReadOnlyList<CaptureItemViewModel> Items => _items;
    public IReadOnlyCollection<string> SelectedIds => _selected;
    /// <summary>Whether the capture is in the current result (its item follows the library's changes).</summary>
    public bool Shows(string id) => _byId.ContainsKey(id);
    public QueueViewModel Queue => _queue;
    internal AppServices Services => _s;

    /// <summary>Raised when the view should bring a row into view (row index of the current mode).</summary>
    public event Action<int>? ScrollToRowRequested;

    // ============================================================================================================ scope and tree

    [ObservableProperty] TreeNodeViewModel? _selectedNode;
    [ObservableProperty] string _scopeTitle = "All captures";
    [ObservableProperty] bool _isLoading;
    [ObservableProperty] int _totalCount;

    bool _keepScope;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? oldValue, TreeNodeViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue == null) return;
        newValue.IsSelected = true;
        if (_keepScope) return;
        ScopeTitle = ScopeTitleOf(newValue);
        _suppressModeSave = true;
        Mode = ReadMode(newValue.Key);
        _suppressModeSave = false;
        _s.Library.Settings.SetValue("browse.lastScope", newValue.Key);
        ClearSelection();
        _ = RequeryAsync();
        _ = RefreshChipCountsAsync();
    }

    string ScopeTitleOf(TreeNodeViewModel n)
    {
        if (n.Kind != TreeNodeKind.Subfolder || n.Path == null) return n.Label;
        // "2026 · Autumn": the sub-folder path below the indexed folder
        var folder = _lib.GetFolders().FirstOrDefault(f => f.Id == n.FolderId);
        if (folder == null) return n.Label;
        var rel = Path.GetRelativePath(folder.Path, n.Path).Replace('\\', '/');
        return rel == "." ? n.Label : rel.Replace("/", " · ");
    }

    [RelayCommand]
    void SelectNode(TreeNodeViewModel? node)
    {
        if (node == null) return;
        if (node.Kind == TreeNodeKind.NewAlbum) { NewAlbum(Array.Empty<string>()); return; }
        if (node.IsGroup) { ToggleNode(node); return; }
        if (!node.IsSelectable) return;
        SelectedNode = node;
    }

    public async Task RefreshTreeAsync()
    {
        _treeCts?.Cancel();
        var cts = _treeCts = new CancellationTokenSource();
        List<TreeNodeViewModel> nodes;
        try { nodes = await Task.Run(BuildTree, cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        string? keep = SelectedNode?.Key ?? _s.Library.Settings.GetValue("browse.lastScope");
        Tree.Clear();
        foreach (var n in nodes) Tree.Add(n);
        var sel = nodes.FirstOrDefault(n => n.Key == keep && n.IsSelectable) ?? nodes.FirstOrDefault(n => n.IsSelectable);
        if (sel != null && sel.Key == SelectedNode?.Key)
        {
            // same scope: keep it without a re-query, just move the highlight to the new node object
            _keepScope = true;
            SelectedNode = sel;
            _keepScope = false;
        }
        else SelectedNode = sel;
    }

    List<TreeNodeViewModel> BuildTree()
    {
        var nodes = new List<TreeNodeViewModel>();
        var collapsed = CollapsedKeys();
        var folders = _lib.GetFolders();
        int all = _lib.Count(new CaptureQuery());
        var root = new TreeNodeViewModel { Kind = TreeNodeKind.All, Label = "All captures", CountText = Text.Number(all), IsHeading = true, Key = "all", Scope = new CaptureQuery() };
        nodes.Add(root);
        foreach (var f in folders)
        {
            var folderNode = new TreeNodeViewModel
            {
                // a folder that cannot be found (for example on a drive that is not connected) says so in the tree; its photos show "Missing"
                Kind = TreeNodeKind.Folder, Label = FolderLabel(f.Path), CountText = f.Exists ? Text.Number(f.CaptureCount) : "not found", Depth = 1, Parent = root,
                Key = $"folder:{f.Id}", Scope = new CaptureQuery { FolderId = f.Id }, FolderId = f.Id, Path = f.Path,
                Note = f.Exists ? null : "This folder cannot be found: its drive may not be connected. Connect it and choose Rescan, or remove the folder in Settings.",
            };
            nodes.Add(folderNode);
            var byRel = new Dictionary<string, TreeNodeViewModel>(StringComparer.Ordinal) { [""] = folderNode };
            foreach (var sub in _lib.GetSubfolders(f.Id).Where(x => x.RelativePath.Length > 0).OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                int slash = sub.RelativePath.LastIndexOf('/');
                var parent = byRel.TryGetValue(slash < 0 ? "" : sub.RelativePath[..slash], out var p) ? p : folderNode;
                var node = new TreeNodeViewModel
                {
                    Kind = TreeNodeKind.Subfolder, Label = Path.GetFileName(sub.RelativePath), CountText = Text.Number(sub.CaptureCount),
                    Depth = parent.Depth + 1, Parent = parent, Key = $"path:{sub.Path}", Scope = new CaptureQuery { PathPrefix = sub.Path },
                    FolderId = f.Id, Path = sub.Path,
                };
                byRel[sub.RelativePath] = node;
                nodes.Add(node);
            }
        }
        var albums = new TreeNodeViewModel { Kind = TreeNodeKind.Heading, Label = "Albums", IsHeading = true, Key = "h:albums" };
        nodes.Add(albums);
        foreach (var a in _lib.GetAlbums())
            nodes.Add(new TreeNodeViewModel
            {
                Kind = TreeNodeKind.Album, Label = a.Name, CountText = Text.Number(a.CaptureCount), Depth = 1, Parent = albums, Key = $"album:{a.Id}",
                Scope = new CaptureQuery { AlbumId = a.Id }, AlbumId = a.Id,
            });
        nodes.Add(new TreeNodeViewModel { Kind = TreeNodeKind.NewAlbum, Label = "New album…", Depth = 1, Parent = albums, Key = "new-album" });
        var smart = new TreeNodeViewModel { Kind = TreeNodeKind.Heading, Label = "Smart filters", IsHeading = true, Key = "h:smart" };
        nodes.Add(smart);
        var counts = _lib.GetSmartFilterCounts();
        int rated4 = _lib.Count(new CaptureQuery { MinRating = 4 });
        void Smart(string label, int n, CaptureQuery q, string key, bool showIfZero = true)
        {
            if (n == 0 && !showIfZero) return;
            nodes.Add(new TreeNodeViewModel { Kind = TreeNodeKind.Smart, Label = label, CountText = Text.Number(n), Depth = 1, Parent = smart, Key = key, Scope = q });
        }
        Smart("Depth edited", counts.GetValueOrDefault(SmartFilter.DepthEdited), new CaptureQuery().With(SmartFilter.DepthEdited), "smart:edited");
        Smart("Rated 4 and up", rated4, new CaptureQuery { MinRating = 4 }, "smart:rated4");
        Smart("Low light (stacked)", counts.GetValueOrDefault(SmartFilter.Stacked), new CaptureQuery().With(SmartFilter.Stacked), "smart:stacked");
        Smart("Telephoto", counts.GetValueOrDefault(SmartFilter.Telephoto), new CaptureQuery().With(SmartFilter.Telephoto), "smart:tele");
        Smart("Not analysed", counts.GetValueOrDefault(SmartFilter.NotAnalysed), new CaptureQuery().With(SmartFilter.NotAnalysed), "smart:notanalysed");
        Smart(SmartFilters.Label(SmartFilter.EditedInLumen), counts.GetValueOrDefault(SmartFilter.EditedInLumen), new CaptureQuery().With(SmartFilter.EditedInLumen), "smart:lumenedited", false);
        Smart(SmartFilters.Label(SmartFilter.HasLumenSession), counts.GetValueOrDefault(SmartFilter.HasLumenSession), new CaptureQuery().With(SmartFilter.HasLumenSession), "smart:lumen", false);
        Smart("Full detail kept", counts.GetValueOrDefault(SmartFilter.FullDetail), new CaptureQuery().With(SmartFilter.FullDetail), "smart:full", false);
        Smart("Missing", counts.GetValueOrDefault(SmartFilter.Missing), new CaptureQuery().With(SmartFilter.Missing), "smart:missing", false);

        foreach (var n in nodes) if (n.Parent != null) n.Parent.HasChildren = true;
        foreach (var n in nodes) n.IsExpanded = !(n.HasChildren && collapsed.Contains(n.Key));
        UpdateShown(nodes);
        return nodes;
    }

    static void UpdateShown(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            bool shown = true;
            for (var p = n.Parent; p != null; p = p.Parent) if (!p.IsExpanded) { shown = false; break; }
            n.IsShown = shown;
        }
    }

    HashSet<string> CollapsedKeys() =>
        (_s.Library.Settings.GetValue("tree.collapsed") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Expand or collapse a row (the chevron, Left / Right); remembered.</summary>
    [RelayCommand]
    public void ToggleNode(TreeNodeViewModel? node)
    {
        if (node is not { HasChildren: true }) return;
        node.IsExpanded = !node.IsExpanded;
        UpdateShown(Tree);
        var keys = Tree.Where(n => n.HasChildren && !n.IsExpanded).Select(n => n.Key);
        _s.Library.Settings.SetValue("tree.collapsed", string.Join('\n', keys));
        // a selection hidden by the collapse moves to the row collapsed
        if (SelectedNode is { IsShown: false }) SelectedNode = node.IsSelectable ? node : Tree.First(n => n.IsSelectable);
    }

    public void SetExpanded(TreeNodeViewModel node, bool expanded) { if (node.HasChildren && node.IsExpanded != expanded) ToggleNode(node); }

    // ---- folder actions (tree context menu, tree header, settings)
    [RelayCommand] Task AddFolder() => _shell.AddFolderAsync();

    [RelayCommand]
    void RescanNode(TreeNodeViewModel? node)
    {
        var lib = _lib;
        if (node?.FolderId is long id) { _ = Task.Run(() => lib.RescanFolder(id)); _shell.Notify($"Rescanning {FolderLabel(_lib.GetFolders().FirstOrDefault(f => f.Id == id)?.Path ?? "the folder")}"); }
        else { _ = Task.Run(() => lib.RescanAll()); _shell.Notify("Rescanning every folder"); }
    }

    [RelayCommand]
    void RevealNode(TreeNodeViewModel? node) { if (node?.Path is { } p) _s.Platform.Reveal(p); }

    [RelayCommand]
    void RemoveFolderNode(TreeNodeViewModel? node)
    {
        if (node is not { Kind: TreeNodeKind.Folder, FolderId: long id, Path: { } path }) return;
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel("Stop using this folder?",
            $"{path} leaves the library. Nothing on disk is touched: the captures, their edits and working copies stay where they are and come back if you add the folder again.",
            "Remove folder",
            () => { _shell.CloseDialog(c!); var lib = _lib; _ = Task.Run(() => lib.RemoveFolderAsync(id)); },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    [RelayCommand]
    void DeleteAlbumNode(TreeNodeViewModel? node)
    {
        if (node is not { AlbumId: long id }) return;
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel($"Delete the album “{node.Label}”?", "The captures stay in the library; only the album goes.", "Delete album",
            () => { _shell.CloseDialog(c!); var lib = _lib; _ = Task.Run(() => lib.DeleteAlbumAsync(id)); },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    static string FolderLabel(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return name.Length > 0 ? name : path;
    }

    // ============================================================================================================ filter, chips, sort

    [ObservableProperty] string _filterText = "";

    partial void OnFilterTextChanged(string value)
    {
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        _ = Task.Delay(220, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) _ = RequeryAsync(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Apply the filter text now (tests; Enter in the field).</summary>
    public Task ApplyFilterNowAsync() { _filterCts?.Cancel(); return RequeryAsync(); }

    [RelayCommand]
    void SelectChip(ChipViewModel chip)
    {
        foreach (var c in Chips) c.IsOn = c == chip;
        _ = RequeryAsync();
    }

    public ChipViewModel ActiveChip => Chips.First(c => c.IsOn);

    async Task RefreshChipCountsAsync()
    {
        var scope = SelectedNode?.Scope ?? new CaptureQuery();
        var counts = await Task.Run(() => _lib.GetSmartFilterCounts(scope)).ConfigureAwait(true);
        var na = Chips.First(c => c.Filter == SmartFilter.NotAnalysed);
        int n = counts.GetValueOrDefault(SmartFilter.NotAnalysed);
        na.Label = n > 0 ? $"Not analysed · {Text.Number(n)}" : "Not analysed";
    }

    [ObservableProperty] CaptureSort _sort = CaptureSort.CaptureTime;
    [ObservableProperty] bool _descending = true;

    public IReadOnlyList<SortChoice> SortChoices { get; } = new SortChoice[]
    {
        new("Capture time, newest first", CaptureSort.CaptureTime, true),
        new("Capture time, oldest first", CaptureSort.CaptureTime, false),
        new("Name", CaptureSort.Name, false),
        new("Rating, highest first", CaptureSort.Rating, true),
        new("Lens", CaptureSort.FocalLength, false),
        new("File size, largest first", CaptureSort.FileSize, true),
    };

    CaptureSort EffectiveSort => Mode == BrowseMode.Timeline ? CaptureSort.CaptureTime : Sort;

    public string SortLabel
    {
        get
        {
            string what = EffectiveSort switch
            {
                CaptureSort.Name => "name", CaptureSort.Rating => "rating", CaptureSort.FocalLength => "lens", CaptureSort.FileSize => "file size",
                _ => "capture time",
            };
            return $"Sort: {what} {(Descending ? "↓" : "↑")}";
        }
    }

    [RelayCommand]
    void ApplySort(SortChoice choice)
    {
        Sort = choice.Sort;
        Descending = choice.Descending;
        AfterSortChange();
    }

    /// <summary>List header click: sort by that column, again to reverse.</summary>
    [RelayCommand]
    void SortBy(string column)
    {
        var s = column switch { "name" => CaptureSort.Name, "rating" => CaptureSort.Rating, "lens" => CaptureSort.FocalLength, "size" => CaptureSort.FileSize, _ => CaptureSort.CaptureTime };
        if (s == Sort) Descending = !Descending;
        else { Sort = s; Descending = s is CaptureSort.CaptureTime or CaptureSort.Rating or CaptureSort.FileSize; }
        AfterSortChange();
    }

    void AfterSortChange()
    {
        _s.Library.Settings.SetValue("browse.sort", $"{Sort}:{Descending}");
        OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(HeaderName)); OnPropertyChanged(nameof(HeaderTime)); OnPropertyChanged(nameof(HeaderLens)); OnPropertyChanged(nameof(HeaderRating));
        _ = RequeryAsync();
    }

    string Arrow(CaptureSort s) => Sort == s ? (Descending ? " ↓" : " ↑") : "";
    public string HeaderName => "Name" + Arrow(CaptureSort.Name);
    public string HeaderTime => "Captured" + Arrow(CaptureSort.CaptureTime);
    public string HeaderLens => "Lens" + Arrow(CaptureSort.FocalLength);
    public string HeaderRating => "Rating" + Arrow(CaptureSort.Rating);

    // ============================================================================================================ view mode and size

    [ObservableProperty] BrowseMode _mode = BrowseMode.Grid;
    bool _suppressModeSave;

    partial void OnModeChanged(BrowseMode oldValue, BrowseMode newValue)
    {
        OnPropertyChanged(nameof(IsGrid)); OnPropertyChanged(nameof(IsTimeline)); OnPropertyChanged(nameof(IsList));
        OnPropertyChanged(nameof(ThumbSize)); OnPropertyChanged(nameof(MinThumbSize)); OnPropertyChanged(nameof(MaxThumbSize));
        OnPropertyChanged(nameof(SortLabel)); OnPropertyChanged(nameof(ShowSizeSlider));
        if (!_suppressModeSave && SelectedNode != null) _s.Library.Settings.SetValue("view:" + SelectedNode.Key, newValue.ToString());
        bool timelineChange = (oldValue == BrowseMode.Timeline) != (newValue == BrowseMode.Timeline);
        if (timelineChange && Sort != CaptureSort.CaptureTime) _ = RequeryAsync();
        else RebuildRows();
    }

    public bool IsGrid => Mode == BrowseMode.Grid;
    public bool IsTimeline => Mode == BrowseMode.Timeline;
    public bool IsList => Mode == BrowseMode.List;
    public bool ShowSizeSlider => Mode != BrowseMode.List;

    [RelayCommand] void SetMode(BrowseMode mode) => Mode = mode;

    BrowseMode ReadMode(string key) =>
        Enum.TryParse<BrowseMode>(_s.Library.Settings.GetValue("view:" + key), out var m) ? m : BrowseMode.Grid;

    double _gridSize, _cardSize;
    public double MinThumbSize => IsTimeline ? MinCardSize : MinGridSize;
    public double MaxThumbSize => IsTimeline ? MaxCardSize : MaxGridSize;

    /// <summary>The size slider: the minimum cell width of the grid or of the timeline cards.</summary>
    public double ThumbSize
    {
        get => IsTimeline ? _cardSize : _gridSize;
        set
        {
            value = Math.Clamp(value, MinThumbSize, MaxThumbSize);
            if (IsTimeline) { if (Math.Abs(_cardSize - value) < 0.5) return; _cardSize = value; }
            else { if (Math.Abs(_gridSize - value) < 0.5) return; _gridSize = value; }
            OnPropertyChanged();
            _s.Library.Settings.SetValue(IsTimeline ? "browse.cardSize" : "browse.gridSize", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            UpdateMetrics();
        }
    }

    double ReadDouble(string key, double fallback) =>
        double.TryParse(_s.Library.Settings.GetValue(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>The browse area's width (the view reports it on resize).</summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 0.5) return;
        _viewportWidth = width;
        UpdateMetrics();
    }

    void UpdateMetrics()
    {
        int gc = Fit(_viewportWidth - 2 * GridPadding, _gridSize, GridGap);
        double gw = Math.Floor((_viewportWidth - 2 * GridPadding - GridGap * (gc - 1)) / gc);
        int cc = Fit(_viewportWidth - 2 * CardPadding, _cardSize, CardGap);
        double cw = Math.Floor((_viewportWidth - 2 * CardPadding - CardGap * (cc - 1)) / cc);
        bool rebuild = gc != GridMetrics.Columns || cc != CardMetrics.Columns;
        GridMetrics.Columns = gc; GridMetrics.CellWidth = gw; GridMetrics.ImageHeight = Math.Round((gw - 10) * 0.75);
        CardMetrics.Columns = cc; CardMetrics.CellWidth = cw; CardMetrics.ImageHeight = Math.Round((cw - 4) * 0.75);
        if (rebuild) RebuildRows();
    }

    static int Fit(double width, double min, double gap) => Math.Max(1, (int)Math.Floor((width + gap) / (min + gap)));

    // ============================================================================================================ the result

    [ObservableProperty] IReadOnlyList<object> _gridRows = Array.Empty<object>();
    [ObservableProperty] IReadOnlyList<object> _timelineRows = Array.Empty<object>();
    [ObservableProperty] IReadOnlyList<CaptureItemViewModel> _listRows = Array.Empty<CaptureItemViewModel>();

    CaptureQuery BuildQuery()
    {
        var q = SelectedNode?.Scope ?? new CaptureQuery();
        q = ActiveChip.Apply(q);
        if (!string.IsNullOrWhiteSpace(FilterText)) q = q with { Text = FilterText.Trim() };
        return q with { Sort = EffectiveSort, Descending = Descending, Offset = 0, Limit = 5000 };
    }

    /// <summary>Load the whole ordered result of the current scope, filters and sort (in pages, off the UI thread).</summary>
    public async Task RequeryAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var q = BuildQuery();
        IsLoading = true;
        List<CaptureItem> result;
        try
        {
            result = await Task.Run(async () =>
            {
                var list = new List<CaptureItem>();
                int offset = 0;
                while (true)
                {
                    var page = await _lib.QueryAsync(q with { Offset = offset }, cts.Token).ConfigureAwait(false);
                    list.AddRange(page.Items);
                    offset += page.Items.Count;
                    if (page.Items.Count == 0 || offset >= page.Total) break;
                }
                return list;
            }, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        IsLoading = false;
        Apply(result);
    }

    void Apply(List<CaptureItem> result)
    {
        string? cursorId = _cursor >= 0 && _cursor < _items.Count ? _items[_cursor].Id : null;
        var next = new List<CaptureItemViewModel>(result.Count);
        var byId = new Dictionary<string, CaptureItemViewModel>(result.Count, StringComparer.Ordinal);
        for (int i = 0; i < result.Count; i++)
        {
            var it = result[i];
            if (_byId.TryGetValue(it.Id, out var vm)) { if (!Equals(vm.Item, it)) UpdateItem(vm, it); }
            else vm = new CaptureItemViewModel(it, _queue);
            vm.Index = i;
            next.Add(vm);
            byId[it.Id] = vm;
        }
        foreach (var gone in _items) if (!byId.ContainsKey(gone.Id)) { gone.IsSelected = false; gone.IsCursor = false; }
        _items = next;
        _byId = byId;
        _selected.RemoveWhere(id => !byId.ContainsKey(id));
        TotalCount = next.Count;
        _cursor = cursorId != null && byId.TryGetValue(cursorId, out var c) ? c.Index : -1;
        if (_anchor >= next.Count) _anchor = -1;
        RebuildRows();
        SelectionChanged();
        ResultChanged?.Invoke();
    }

    /// <summary>The ordered result was loaded again (the Develop page's filmstrip follows).</summary>
    public event Action? ResultChanged;

    void UpdateItem(CaptureItemViewModel vm, CaptureItem it)
    {
        var old = vm.Item;
        if (old.ThumbnailCurrent != it.ThumbnailCurrent || old.HasThumbnail != it.HasThumbnail)
            _s.Thumbnails.Invalidate(it.Id);
        vm.Update(it);
    }

    void RebuildRows()
    {
        switch (Mode)
        {
            case BrowseMode.Grid:
            {
                int n = Math.Max(1, GridMetrics.Columns);
                var rows = new List<object>((_items.Count + n - 1) / n);
                _rowOfItem = new int[_items.Count];
                for (int i = 0, r = 0; i < _items.Count; i += n, r++)
                {
                    int count = Math.Min(n, _items.Count - i);
                    rows.Add(new GridRowViewModel(_items.GetRange(i, count), GridMetrics, r));
                    for (int k = 0; k < count; k++) _rowOfItem[i + k] = r;
                }
                GridRows = rows;
                break;
            }
            case BrowseMode.Timeline:
            {
                int n = Math.Max(1, CardMetrics.Columns);
                var rows = new List<object>();
                _rowOfItem = new int[_items.Count];
                for (int i = 0; i < _items.Count;)
                {
                    var day = DayOf(_items[i]);
                    int j = i;
                    while (j < _items.Count && DayOf(_items[j]) == day) j++;
                    int count = j - i;
                    rows.Add(new TimelineHeaderViewModel(this, Text.Day(day), Text.Count(count, "capture", "captures"), i, count));
                    for (int k = i; k < j; k += n)
                    {
                        int c = Math.Min(n, j - k);
                        for (int m = 0; m < c; m++) _rowOfItem[k + m] = rows.Count;
                        rows.Add(new TimelineRowViewModel(_items.GetRange(k, c), CardMetrics, k + c >= j));
                    }
                    i = j;
                }
                TimelineRows = rows;
                break;
            }
            default:
                _rowOfItem = Enumerable.Range(0, _items.Count).ToArray();
                ListRows = _items.ToList();
                break;
        }
    }

    static DateOnly? DayOf(CaptureItemViewModel vm) => vm.Item.CaptureTime is DateTime t ? DateOnly.FromDateTime(t) : null;

    int RowOf(int index) => index >= 0 && index < _rowOfItem.Length ? _rowOfItem[index] : -1;

    // ============================================================================================================ change events

    void OnCapturesChanged(IReadOnlyCollection<string> ids, bool structural)
    {
        if (structural) { ScheduleRequery(); _ = RefreshTreeAsync(); _ = RefreshChipCountsAsync(); return; }
        bool affectsOrder = false;
        foreach (var id in ids)
        {
            if (!_byId.TryGetValue(id, out var vm)) continue;
            var it = _lib.Get(id);
            if (it == null) { affectsOrder = true; continue; }
            var old = vm.Item;
            if (Equals(old, it)) continue;
            if (old.Rating != it.Rating || old.CacheLevel != it.CacheLevel || old.DepthState != it.DepthState || old.Edits != it.Edits || old.Missing != it.Missing)
                affectsOrder = true;
            UpdateItem(vm, it);
        }
        if (Details.Current is { } cur && ids.Contains(cur.Id)) Details.Refresh();
        if (affectsOrder && (BuildQuery() is var q) && (q.Sort == CaptureSort.Rating || q.MinRating > 0 || q.DepthEdited != null || q.Analysed != null || q.CacheLevel != null || q.Missing != null))
            ScheduleRequery();
        if (affectsOrder) { _ = RefreshChipCountsAsync(); ScheduleTreeRefresh(); }
    }

    bool _treePending;
    void ScheduleTreeRefresh()
    {
        if (_treePending) return;
        _treePending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _treePending = false; _ = RefreshTreeCountsAsync(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Counts changed (rating, analysis): rebuild the tree but keep the scope.</summary>
    Task RefreshTreeCountsAsync() => RefreshTreeAsync();

    void ScheduleRequery()
    {
        if (_requeryPending) return;
        _requeryPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _requeryPending = false; _ = RequeryAsync(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ============================================================================================================ selection

    [ObservableProperty] int _selectedCount;

    public string CountsText => SelectedCount > 0
        ? $"{Text.Count(TotalCount, "capture", "captures")} · {Text.Number(SelectedCount)} selected"
        : Text.Count(TotalCount, "capture", "captures");

    partial void OnTotalCountChanged(int value) { OnPropertyChanged(nameof(CountsText)); OnPropertyChanged(nameof(IsEmptyResult)); }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmptyResult));
    public bool IsEmptyResult => TotalCount == 0 && !IsLoading;

    public bool HasSelection => SelectedCount > 0;
    /// <summary>The selection bar: there is a selection and the grid is on screen (the Animate panel's preview covers the grid).</summary>
    public bool ShowSelectionBar => HasSelection && !IsAnimateOpen;
    /// <summary>The bars over the grid (title, sort, size and the filter chips): hidden while the Animate panel's preview covers the grid.</summary>
    public bool ShowBrowseBars => !IsAnimateOpen;
    public string SelectionTitle => $"{Text.Number(SelectedCount)} selected";
    public string ExportLabel => SelectedCount > 1 ? $"Export {Text.Number(SelectedCount)}…" : "Export…";
    public int CursorIndex => _cursor;
    public CaptureItemViewModel? CursorItem => _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : null;

    void SelectionChanged()
    {
        SelectedCount = _selected.Count;
        OnPropertyChanged(nameof(CountsText)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(ShowSelectionBar)); OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(ExportLabel)); OnPropertyChanged(nameof(CursorItem));
        OnPropertyChanged(nameof(CanCopyAdjustments)); OnPropertyChanged(nameof(CanPasteAdjustments)); OnPropertyChanged(nameof(PasteLabel));
        Details.Refresh();
    }

    void SetSelected(CaptureItemViewModel vm, bool on)
    {
        vm.IsSelected = on;
        if (on) _selected.Add(vm.Id); else _selected.Remove(vm.Id);
    }

    void MoveCursorTo(int index, bool scroll = true)
    {
        if (_cursor >= 0 && _cursor < _items.Count) _items[_cursor].IsCursor = false;
        _cursor = index;
        if (_cursor >= 0 && _cursor < _items.Count)
        {
            _items[_cursor].IsCursor = true;
            if (scroll) ScrollToRowRequested?.Invoke(RowOf(_cursor));
        }
        OnPropertyChanged(nameof(CursorItem));
        OnPropertyChanged(nameof(CursorIndex));
    }

    /// <summary>A click: plain = only this; Ctrl = toggle; Shift = range from the anchor.</summary>
    public void Click(CaptureItemViewModel item, bool ctrl, bool shift)
    {
        int i = item.Index;
        if (shift && _anchor >= 0)
        {
            if (!ctrl) foreach (var id in _selected.ToList()) if (_byId.TryGetValue(id, out var v)) v.IsSelected = false;
            if (!ctrl) _selected.Clear();
            int a = Math.Min(_anchor, i), b = Math.Max(_anchor, i);
            for (int k = a; k <= b; k++) SetSelected(_items[k], true);
        }
        else if (ctrl)
        {
            SetSelected(item, !item.IsSelected);
            _anchor = i;
        }
        else
        {
            foreach (var id in _selected.ToList()) if (_byId.TryGetValue(id, out var v)) v.IsSelected = false;
            _selected.Clear();
            SetSelected(item, true);
            _anchor = i;
        }
        MoveCursorTo(i, scroll: false);
        SelectionChanged();
    }

    /// <summary>Right-click: keep a selection that contains the item, else select just it.</summary>
    public void ContextClick(CaptureItemViewModel item)
    {
        if (!item.IsSelected) Click(item, false, false);
        else MoveCursorTo(item.Index, scroll: false);
    }

    /// <summary>Arrow keys and friends. <paramref name="delta"/> in items (rows in the timeline are handled by <see cref="MoveVertical"/>).</summary>
    public void MoveCursor(int delta, bool shift, bool ctrl)
    {
        if (_items.Count == 0) return;
        int target = _cursor < 0 ? 0 : Math.Clamp(_cursor + delta, 0, _items.Count - 1);
        MoveTo(target, shift, ctrl);
    }

    public void MoveTo(int target, bool shift, bool ctrl)
    {
        if (_items.Count == 0) return;
        target = Math.Clamp(target, 0, _items.Count - 1);
        if (shift)
        {
            if (_anchor < 0) _anchor = Math.Max(0, _cursor);
            foreach (var id in _selected.ToList()) if (_byId.TryGetValue(id, out var v)) v.IsSelected = false;
            _selected.Clear();
            int a = Math.Min(_anchor, target), b = Math.Max(_anchor, target);
            for (int k = a; k <= b; k++) SetSelected(_items[k], true);
        }
        else if (!ctrl)
        {
            foreach (var id in _selected.ToList()) if (_byId.TryGetValue(id, out var v)) v.IsSelected = false;
            _selected.Clear();
            SetSelected(_items[target], true);
            _anchor = target;
        }
        MoveCursorTo(target);
        SelectionChanged();
    }

    /// <summary>Up / down: a row of the current layout.</summary>
    public void MoveVertical(int rows, bool shift, bool ctrl)
    {
        if (_items.Count == 0) return;
        if (_cursor < 0) { MoveTo(0, shift, ctrl); return; }
        switch (Mode)
        {
            case BrowseMode.Grid: MoveCursor(rows * Math.Max(1, GridMetrics.Columns), shift, ctrl); break;
            case BrowseMode.List: MoveCursor(rows, shift, ctrl); break;
            default:
            {
                // timeline: same column in the next / previous card row
                var rowsList = TimelineRows;
                int r = RowOf(_cursor);
                if (r < 0 || rowsList[r] is not TimelineRowViewModel cur) { MoveCursor(rows, shift, ctrl); return; }
                int col = cur.Cells.ToList().FindIndex(c => c.Index == _cursor);
                int step = Math.Sign(rows), left = Math.Abs(rows);
                int k = r;
                while (left > 0)
                {
                    k += step;
                    if (k < 0 || k >= rowsList.Count) break;
                    if (rowsList[k] is TimelineRowViewModel) { left--; if (left == 0) break; }
                }
                if (k < 0 || k >= rowsList.Count || rowsList[k] is not TimelineRowViewModel target)
                {
                    MoveTo(step < 0 ? 0 : _items.Count - 1, shift, ctrl);
                    return;
                }
                MoveTo(target.Cells[Math.Min(col, target.Cells.Count - 1)].Index, shift, ctrl);
                break;
            }
        }
    }

    public int PageItems(double viewportHeight)
    {
        double rowH = Mode switch
        {
            BrowseMode.Grid => GridMetrics.ImageHeight + 32,
            BrowseMode.Timeline => CardMetrics.ImageHeight + 70,
            _ => 48,
        };
        return Math.Max(1, (int)(viewportHeight / Math.Max(1, rowH)));
    }

    [RelayCommand]
    public void ToggleCursor()
    {
        if (CursorItem is not { } vm) return;
        SetSelected(vm, !vm.IsSelected);
        _anchor = vm.Index;
        SelectionChanged();
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var vm in _items) SetSelected(vm, true);
        if (_cursor < 0 && _items.Count > 0) MoveCursorTo(0, scroll: false);
        SelectionChanged();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var id in _selected.ToList()) if (_byId.TryGetValue(id, out var v)) v.IsSelected = false;
        _selected.Clear();
        SelectionChanged();
    }

    public void SelectRange(int first, int count)
    {
        ClearSelection();
        for (int k = first; k < first + count && k < _items.Count; k++) SetSelected(_items[k], true);
        _anchor = first;
        MoveCursorTo(first, scroll: false);
        SelectionChanged();
    }

    /// <summary>Select exactly these captures (where present in the current result).</summary>
    public void SelectIds(IEnumerable<string> ids)
    {
        ClearSelection();
        int first = -1;
        foreach (var id in ids) if (_byId.TryGetValue(id, out var vm)) { SetSelected(vm, true); if (first < 0 || vm.Index < first) first = vm.Index; }
        if (first >= 0) MoveCursorTo(first);
        SelectionChanged();
    }

    /// <summary>The captures an action applies to: the selection, else the cursor's capture.</summary>
    public IReadOnlyList<string> Targets()
    {
        if (_selected.Count > 0) return _items.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        return CursorItem is { } c ? new[] { c.Id } : Array.Empty<string>();
    }

    // ============================================================================================================ actions

    /// <summary>Keys 0–5: rate the selection (or the cursor's capture).</summary>
    public void SetRating(int rating)
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        // show it at once; the library's change event confirms
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var vm)) vm.Update(vm.Item with { Rating = rating, RatingSource = rating == 0 ? RatingSource.None : vm.Item.RatingSource });
        Details.Refresh();
        _ = Task.Run(() => _lib.SetRatingsAsync(ids, rating));
    }

    [RelayCommand] void Rate(string rating) => SetRating(int.Parse(rating, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Rate one capture (the Develop page's open photo): shown at once, saved with the photo off the UI thread.</summary>
    public void RateCapture(CaptureItemViewModel item, int rating)
    {
        item.Update(item.Item with { Rating = rating, RatingSource = rating == 0 ? RatingSource.None : item.Item.RatingSource });
        if (_byId.TryGetValue(item.Id, out var vm) && vm != item) vm.Update(vm.Item with { Rating = rating });
        Details.Refresh();
        var id = item.Id;
        _ = Task.Run(() => _lib.SetRatingAsync(id, rating));
    }

    [RelayCommand] Task UpgradeSelected() => ChangeQualityOfTargets(ViewQuality.FullDetail);
    [RelayCommand] Task ReleaseSelected() => ChangeQualityOfTargets(ViewQuality.Linear13);

    /// <summary>The selection bar and the context menu: the same notice as the quality selector, for the whole selection, as a dialog.</summary>
    async Task ChangeQualityOfTargets(ViewQuality target)
    {
        var items = Targets().Select(id => _byId.TryGetValue(id, out var vm) ? vm.Item : null).OfType<CaptureItem>().ToList();
        if (items.Count == 0) return;
        var notice = await QualityChanges.PrepareAsync(items, target, n => { if (n != null) _shell.CloseDialog(n); }).ConfigureAwait(true);
        if (notice != null) _shell.ShowDialog(notice);
    }

    [RelayCommand]
    void AnalyseSelected()
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        _ = Task.Run(() => _lib.AnalyseNow(ids));
        _shell.Notify(ids.Count == 1 ? "Analysis queued" : $"Analysis of {ids.Count} captures queued");
    }

    [RelayCommand]
    void OpenInDevelop()
    {
        var item = Details.Current ?? CursorItem;
        if (item != null) _shell.OpenInDevelop(item);
    }

    [RelayCommand]
    void RevealSelected()
    {
        if ((Details.Current ?? CursorItem) is { } c && !c.Missing) _s.Platform.Reveal(c.Item.Path);
    }

    // ---- copy / paste adjustments (the selection bar, the context menu)

    public bool CanCopyAdjustments => Targets().Count == 1 && _s.Develop.IsAvailable;
    public bool CanPasteAdjustments => _s.Develop.Clipboard != null && Targets().Count > 0;
    public string PasteLabel => SelectedCount > 1 ? $"Paste to {Text.Number(SelectedCount)}" : "Paste";

    /// <summary>Copy adjustments… of one capture: its saved edits, then the groups to keep (the same dialog as in Develop).</summary>
    [RelayCommand]
    async Task CopyAdjustments()
    {
        var ids = Targets();
        if (ids.Count != 1 || !_byId.TryGetValue(ids[0], out var item)) return;
        var svc = _s.Develop;
        Develop.EditSet set;
        try { set = await Task.Run(() => svc.CopyAsync(item.Id, item.Name, Lux.Library.EditGroups.All, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception e) when (e is LibraryException or NotSupportedException) { _shell.Notify(e.Message); return; }
        PatchDialogViewModel? d = null;
        d = new PatchDialogViewModel(PatchKind.Copy, "Copy adjustments", $"Choose what to copy from {item.Name}", "Copy", set, svc.DefaultSpecs,
            Lux.Library.EditGroups.ToneAndColour,
            vm =>
            {
                svc.SetClipboard(vm.ChosenSet, item.Name, item.Id);
                _shell.CloseDialog(d!);
                _shell.Notify("Adjustments copied — select photos and choose Paste");
                return Task.FromResult<string?>(null);
            }, () => _shell.CloseDialog(d!), svc.SupportsPerSlider);
        _shell.ShowDialog(d);
    }

    /// <summary>Paste the copied adjustments to the selection: one batch, undoable from the notice.</summary>
    [RelayCommand]
    async Task PasteAdjustments()
    {
        var ids = Targets();
        var svc = _s.Develop;
        if (ids.Count == 0 || svc.Clipboard is not { } c) return;
        Develop.PasteResult r;
        try { r = await Task.Run(() => svc.PasteAsync(ids, c.Groups, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception e) when (e is LibraryException or NotSupportedException) { _shell.Notify(e.Message); return; }
        string msg = r.Written == 1 ? "Adjustments pasted to 1 photo" : $"Adjustments pasted to {Text.Number(r.Written)} photos";
        if (r.CropSkipped > 0) msg += $" · crop skipped on {Text.Number(r.CropSkipped)} (another shape)";
        if (r.Failures.Count > 0) msg += $" · {Text.Number(r.Failures.Count)} could not be changed";
        long batch = r.BatchId;
        if (batch <= 0) { _shell.Notify(msg + (r.Written > 0 ? " — undo it in Develop" : "")); return; }
        _shell.Notify(msg, "Undo", () => _ = Task.Run(() => svc.UndoBatchAsync(batch, CancellationToken.None)).ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _shell.Notify(t.IsCompletedSuccessfully && t.Result ? "Paste undone" : "The paste can no longer be undone")),
            TaskScheduler.Default));
    }

    // ---- presets on the selection (the selection bar's flyout, the context menu)

    public ObservableCollection<PresetViewModel> Presets { get; } = new();
    public bool HasPresets => Presets.Count > 0;

    void RefreshPresets()
    {
        Presets.Clear();
        var specs = _s.Develop.DefaultSpecs;
        foreach (var p in _s.Develop.Presets) Presets.Add(new PresetViewModel(p, p.Set.Describe(specs)));
        OnPropertyChanged(nameof(HasPresets));
    }

    /// <summary>Apply a preset to the selection (or the cursor's capture): one batch, undoable from the notice.</summary>
    [RelayCommand]
    async Task ApplyPresetToSelection(PresetViewModel? preset)
    {
        var ids = Targets();
        var svc = _s.Develop;
        if (preset == null || ids.Count == 0) return;
        Develop.PasteResult r;
        try { r = await Task.Run(() => svc.ApplyPresetAsync(preset.Preset.Id, ids, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception e) when (e is LibraryException or NotSupportedException) { _shell.Notify(e.Message); return; }
        string msg = r.Written == 1 ? $"Preset “{preset.Name}” applied to 1 photo" : $"Preset “{preset.Name}” applied to {Text.Number(r.Written)} photos";
        if (r.CropSkipped > 0) msg += $" · crop skipped on {Text.Number(r.CropSkipped)} (another shape)";
        if (r.Failures.Count > 0) msg += $" · {Text.Number(r.Failures.Count)} could not be changed";
        long batch = r.BatchId;
        if (batch <= 0) { _shell.Notify(msg); return; }
        _shell.Notify(msg, "Undo", () => _ = Task.Run(() => svc.UndoBatchAsync(batch, CancellationToken.None)).ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _shell.Notify(t.IsCompletedSuccessfully && t.Result ? "Preset undone" : "The preset can no longer be undone")),
            TaskScheduler.Default));
    }

    // ---- export side panel
    [ObservableProperty] ExportPanelViewModel? _exportPanel;
    public bool IsExportOpen => ExportPanel != null;
    public bool IsSidePanelOpen => IsExportOpen || IsAnimateOpen;
    partial void OnExportPanelChanged(ExportPanelViewModel? value) { OnPropertyChanged(nameof(IsExportOpen)); OnPropertyChanged(nameof(IsSidePanelOpen)); }

    [RelayCommand]
    public void OpenExport()
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        var items = ids.Select(id => _byId[id]).ToList();
        AnimatePanel = null;
        ExportPanel = new ExportPanelViewModel(_s, items, _queue, CloseExport, _shell.Notify, openAnimate: OpenAnimate) { AskAboutExisting = (n, total) => ExportQuestions.AskAsync(_shell, n, total) };
    }

    public void CloseExport() => ExportPanel = null;

    // ---- the Animate side panel: its preview plays over the browse area
    [ObservableProperty] AnimatePanelViewModel? _animatePanel;
    public bool IsAnimateOpen => AnimatePanel != null;
    partial void OnAnimatePanelChanged(AnimatePanelViewModel? oldValue, AnimatePanelViewModel? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(IsAnimateOpen)); OnPropertyChanged(nameof(IsSidePanelOpen));
        OnPropertyChanged(nameof(ShowSelectionBar)); OnPropertyChanged(nameof(ShowBrowseBars));
    }

    [RelayCommand]
    public void OpenAnimate()
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        var items = ids.Select(id => _byId[id]).ToList();
        ExportPanel = null;
        AnimatePanel = new AnimatePanelViewModel(_s, items, _queue, CloseAnimate, _shell.Notify);
    }

    public void CloseAnimate() => AnimatePanel = null;

    // ---- albums
    public IReadOnlyList<Album> Albums() => _lib.GetAlbums();

    public void AddToAlbum(long albumId, IReadOnlyCollection<string> ids, string albumName)
    {
        if (ids.Count == 0) return;
        _ = Task.Run(() => _lib.AddToAlbumAsync(albumId, ids));
        _shell.Notify(ids.Count == 1 ? $"Added to {albumName}" : $"Added {ids.Count} captures to {albumName}");
    }

    public void NewAlbum(IReadOnlyCollection<string> withCaptures)
    {
        var ids = withCaptures.ToList();
        PromptViewModel? p = null;
        p = new PromptViewModel("New album", "Name", ids.Count > 0 ? $"Create and add {Text.Count(ids.Count, "capture", "captures")}" : "Create album",
            async name =>
            {
                try
                {
                    var album = await Task.Run(() => _lib.CreateAlbumAsync(name)).ConfigureAwait(true);
                    if (ids.Count > 0) await Task.Run(() => _lib.AddToAlbumAsync(album.Id, ids)).ConfigureAwait(true);
                    _shell.CloseDialog(p!);
                    _shell.Notify($"Album “{name}” created");
                    return null;
                }
                catch (LibraryException e) { return e.Message; }
            }, () => _shell.CloseDialog(p!));
        _shell.ShowDialog(p);
    }

    [RelayCommand]
    void RemoveFromLibrary()
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        ConfirmViewModel? c = null;
        c = new ConfirmViewModel(
            ids.Count == 1 ? "Remove this capture from the library?" : $"Remove {ids.Count} captures from the library?",
            "Lux stops showing them. No file is changed or deleted: the capture files, their edits and their working copies stay where they are.",
            "Remove from library",
            () => { _ = Task.Run(() => _lib.RemoveFromLibraryAsync(ids)); _shell.CloseDialog(c!); ClearSelection(); },
            () => _shell.CloseDialog(c!));
        _shell.ShowDialog(c);
    }

    [RelayCommand]
    void ImportLumenForSelection()
    {
        var ids = Targets();
        if (ids.Count == 0) { ImportLumenForScope(); return; }
        _shell.OpenLumenImport(new CaptureQuery(), $"{Text.Count(ids.Count, "selected capture", "selected captures")}", ids);
    }

    [RelayCommand]
    void ImportLumenForScope()
    {
        var scope = SelectedNode?.Scope ?? new CaptureQuery();
        _shell.OpenLumenImport(scope with { Text = null }, SelectedNode?.Path ?? ScopeTitle);
    }

    /// <summary>Drag-and-drop or menu: the current targets into an album.</summary>
    public void DropOnAlbum(TreeNodeViewModel node, IReadOnlyCollection<string> ids)
    {
        if (node.AlbumId is long a) AddToAlbum(a, ids, node.Label);
    }

    [RelayCommand] void ShowSettings() => _shell.ShowSettings();

    internal void Notify(string message) => _shell.Notify(message);
}
