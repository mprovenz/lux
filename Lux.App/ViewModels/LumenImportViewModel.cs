using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.App.Services;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>One of the three groups of the Lumen import dialog.</summary>
public sealed partial class LumenGroupViewModel : ObservableObject
{
    readonly Action _changed;
    public LumenGroupViewModel(IReadOnlyList<string> ids, string name, string note, bool on, Action changed, bool hideEmpty = false)
    {
        Ids = ids; Name = name; Note = note; _isOn = on && ids.Count > 0; _changed = changed;
        IsShown = !hideEmpty || ids.Count > 0;
    }
    /// <summary>Listed (one capture asked for by name: only its own group).</summary>
    public bool IsShown { get; }
    public IReadOnlyList<string> Ids { get; }
    public int Count => Ids.Count;
    public string CountText => Text.Number(Ids.Count);
    public string Name { get; }
    public string Note { get; }
    public bool IsAvailable => Ids.Count > 0;
    [ObservableProperty] bool _isOn;
    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// "Import edits from Lumen" (S-Import): finds the Lumen sessions of a folder (or of selected captures), shows them in three groups with
/// the fidelity warning and the tone-style option, and queues the imports.
/// </summary>
public sealed partial class LumenImportViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly Action _close;
    readonly Action<IReadOnlyCollection<string>> _show;
    readonly Action<string> _notify;
    readonly CancellationTokenSource _cts = new();

    public LumenImportViewModel(AppServices s, string label, Func<CancellationToken, Task<CaptureQuery>> scope,
                                IReadOnlyCollection<string>? only, Action close, Action<IReadOnlyCollection<string>> show, Action<string> notify,
                                bool singleCapture = false)
    {
        SingleCapture = singleCapture;
        _s = s;
        _close = close;
        _show = show;
        _notify = notify;
        _label = Tilde(label);
        _ = ScanAsync(scope, only);
    }

    static string Tilde(string p)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && p.StartsWith(home, StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
    }

    [ObservableProperty] string _label;
    [ObservableProperty] bool _isScanning = true;
    [ObservableProperty] string _scanText = "Looking for Lumen sessions…";
    [ObservableProperty] string? _error;
    [ObservableProperty] bool _newerToneStyle;
    public ObservableCollection<LumenGroupViewModel> Groups { get; } = new();

    public bool IsReady => !IsScanning && Error == null;
    public int Total => Groups.Sum(g => g.Count);
    public int Chosen => Groups.Where(g => g.IsOn).Sum(g => g.Count);
    public string Subtitle => IsScanning ? Label : $"{Label} · {Text.Count(Total, "capture with a Lumen session", "captures with a Lumen session")}";
    public string ImportLabel => Chosen == 1 ? "Import 1 capture" : $"Import {Text.Number(Chosen)} captures";
    public string ShowLabel => Chosen == 1 ? "Show the capture" : $"Show the {Text.Number(Chosen)} captures";
    public bool CanImport => IsReady && Chosen > 0;

    public static string WarningTitle => "Imported edits are slightly lower fidelity than editing again in Lux";
    public static string WarningText1 => "Lumen saved its depth map in a compact, rounded form. An imported capture uses that depth map throughout, so it looks the way it did in Lumen, but further editing and processing of it starts from less precise depth than a clean Lux start. The rounding is under 1 mm at 2 m, about 15 mm at 10 m and about 0.4 m at 50 m.";
    public static string WarningText2 => "Each capture keeps a clean Lux version next to the imported one. You can switch between them at any time, and nothing in the Lumen files is changed.";
    public static string ToneTitle => "Use Lumen's newer tone style for these captures";
    public string ToneLabel => SingleCapture ? "Use Lumen's newer tone style for this capture" : ToneTitle;
    public string SpaceText => SingleCapture ? "about 26 MB" : "about 26 MB per capture";
    public static string ToneNote => "Lumen does not record which tone style a capture was exported with. Tick this if your Lumen exports of these photos look different from Lux's.";

    async Task ScanAsync(Func<CancellationToken, Task<CaptureQuery>> scope, IReadOnlyCollection<string>? only)
    {
        try
        {
            var q = await scope(_cts.Token).ConfigureAwait(true);
            ScanText = "Looking for Lumen sessions…";
            var r = await _s.Lumen.ScanAsync(q, Label, _cts.Token).ConfigureAwait(true);
            if (only != null)
            {
                var set = only.ToHashSet(StringComparer.Ordinal);
                r = r with { DepthEdited = r.DepthEdited.Where(set.Contains).ToList(), SettingsChanged = r.SettingsChanged.Where(set.Contains).ToList(), Unchanged = r.Unchanged.Where(set.Contains).ToList() };
            }
            Groups.Add(new LumenGroupViewModel(r.DepthEdited, "Depth edited in Lumen", "Refine edge, blend area or match depth was used", true, Changed, SingleCapture));
            Groups.Add(new LumenGroupViewModel(r.SettingsChanged, "Aperture, focus, tone or crop changed", "No depth edits, but settings differ from a fresh capture", true, Changed, SingleCapture));
            // one capture asked for by name (from Develop): its group is ticked whatever it is
            Groups.Add(new LumenGroupViewModel(r.Unchanged, "Opened in Lumen but unchanged", "Nothing to bring across; Lux uses its own depth map", SingleCapture, Changed, SingleCapture));
            if (r.Total == 0) Error = "No Lumen sessions were found here. Lumen keeps a .lris file beside each capture it opened.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Error = "The folder could not be searched for Lumen sessions. " + Text.Plain(e); }
        IsScanning = false;
        Changed();
    }

    void Changed()
    {
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(Chosen)); OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ImportLabel)); OnPropertyChanged(nameof(ShowLabel)); OnPropertyChanged(nameof(CanImport));
    }

    IReadOnlyList<string> ChosenIds() => Groups.Where(g => g.IsOn).SelectMany(g => g.Ids).ToList();

    /// <summary>The dialog is for one capture (opened from Develop): no "show the captures" link, the wording is singular.</summary>
    public bool SingleCapture { get; }
    public bool ShowShowLink => !SingleCapture;
    /// <summary>Import through someone else (the open photo's session) instead of the library's import jobs.</summary>
    public Func<IReadOnlyList<string>, bool, Task>? ImportOverride { get; init; }

    [RelayCommand]
    void Import()
    {
        if (!CanImport) return;
        var ids = ChosenIds();
        bool tone = NewerToneStyle;
        if (ImportOverride is { } o) { _close(); _ = o(ids, tone); return; }
        var lumen = _s.Lumen;
        _ = Task.Run(() => lumen.Import(ids, tone));
        _notify(ids.Count == 1 ? "Import of Lumen edits queued" : $"Import of {Text.Number(ids.Count)} Lumen sessions queued");
        _close();
    }

    [RelayCommand]
    void ShowCaptures()
    {
        var ids = ChosenIds();
        _close();
        _show(ids);
    }

    [RelayCommand]
    void Cancel()
    {
        _cts.Cancel();
        _close();
    }
}
