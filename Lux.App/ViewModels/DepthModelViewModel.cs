using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lux.DepthModel;

namespace Lux.App.ViewModels;

/// <summary>Settings → Refined depth: the state of the depth model's files, their download and their removal, and where they are stored.</summary>
public sealed partial class DepthModelViewModel : ObservableObject
{
    readonly IDepthModelService _model;

    public DepthModelViewModel(IDepthModelService model)
    {
        _model = model;
        _model.StatusChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Raise);
    }

    void Raise()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(Progress));
    }

    /// <summary>"107 MB".</summary>
    public static string Size(long bytes) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB");

    public const string Description =
        "Refined depth corrects the outlines of objects, thin objects and the sky in a photo's depth map. It uses a depth network, "
        + DepthModelManifest.ModelName + " (" + DepthModelManifest.ModelLicense + "), and " + DepthModelManifest.RuntimeName + " (" + DepthModelManifest.RuntimeLicense
        + ") to run it. Both are downloaded once and are not part of Lux. Use “Refine depth” in Develop to make a photo's refined depth.";
    public string DescriptionText => Description;

    public bool IsAvailable => _model.Status.State != DepthModelState.NotAvailable;

    public string StateText => _model.Status switch
    {
        { State: DepthModelState.NotAvailable } s => s.Reason ?? "Refined depth is not available on this computer.",
        { State: DepthModelState.Downloading } s => $"Downloading · {Size(s.BytesDone)} of {Size(s.BytesTotal)}",
        { State: DepthModelState.Installed } s => $"Installed · {s.Version} · {Size(s.SizeOnDisk)}",
        { State: DepthModelState.Failed } s => s.Reason ?? "The download failed.",
        _ => "Not installed.",
    };

    public string DownloadLabel => $"Download (about {Size(_model.DownloadBytes)})";
    public string FolderText => IsAvailable ? "The files are stored in " + _model.Folder : "";

    public bool CanDownload => _model.Status.State is DepthModelState.NotInstalled or DepthModelState.Failed;
    public bool CanRemove => _model.Status.State is DepthModelState.Installed or DepthModelState.Failed;
    public bool IsDownloading => _model.Status.State == DepthModelState.Downloading;
    public double Progress => _model.Status is { State: DepthModelState.Downloading, BytesTotal: > 0 } s ? 100.0 * s.BytesDone / s.BytesTotal : 0;

    [RelayCommand]
    Task Download() => CanDownload ? _model.DownloadAsync() : Task.CompletedTask;

    [RelayCommand]
    void CancelDownload() => _model.CancelDownload();

    /// <summary>Removal waits for a running download to stop, so it runs off the UI thread.</summary>
    [RelayCommand]
    Task Remove() => Task.Run(_model.Remove);
}
