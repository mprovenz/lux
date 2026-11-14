using CommunityToolkit.Mvvm.ComponentModel;
using Lux.App.Services;
using Lux.Engine.Session;
using Lux.Library;

namespace Lux.App.ViewModels;

/// <summary>One capture as the browse views show it (one per capture in the current scope, reused across re-queries).</summary>
public sealed class CaptureItemViewModel : ObservableObject
{
    readonly QueueViewModel _queue;
    CaptureItem _item;
    bool _isSelected, _isCursor;
    int _thumbVersion;

    public CaptureItemViewModel(CaptureItem item, QueueViewModel queue)
    {
        _item = item;
        _queue = queue;
    }

    public CaptureItem Item => _item;
    public string Id => _item.Id;
    /// <summary>Position in the current ordered result.</summary>
    public int Index { get; internal set; }

    public void Update(CaptureItem item)
    {
        bool thumbChanged = item.HasThumbnail != _item.HasThumbnail || (item.ThumbnailCurrent && !_item.ThumbnailCurrent) || item.Missing != _item.Missing;
        _item = item;
        OnPropertyChanged(string.Empty);
        if (thumbChanged) ThumbVersion++;
    }

    /// <summary>A job of this capture started or ended (the cache tag says "queued" / "analysing").</summary>
    public void JobStateChanged()
    {
        OnPropertyChanged(nameof(CacheTag));
        OnPropertyChanged(nameof(CacheWord));
        OnPropertyChanged(nameof(DepthText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(ProcessingProgress));
    }

    /// <summary>A better quality is being made (analysis, a working copy, full detail — waiting or running): the thumbnail shows blurred
    /// under the spinner in every view.</summary>
    public bool IsProcessing => Quality is { Target: { } t } st && t > st.Cached;
    /// <summary>Its progress 0–1 while running; NaN while it waits.</summary>
    public double ProcessingProgress => Quality is { IsRunning: true } st ? st.Progress : double.NaN;

    /// <summary>The quality kept now and the one a job is bringing it to.</summary>
    public Services.QualityStatus Quality => Services.QualityRules.Status(_item, _queue.ActiveJobOf(_item.Id));

    /// <summary>Clockwise rotation that shows the capture upright.</summary>
    public int Orientation => _item.DisplayRotation;

    // ---- where the photo's data lives (markers)
    /// <summary>The rating could not be saved with the photo, nor in this computer's photo-data folder.</summary>
    public bool NotSavedWithPhoto => _item.RatingNotSavedWithPhoto;
    /// <summary>The photo's data is kept on this computer (its folder cannot be written).</summary>
    public bool InLocalStore => _item.ContextLocation == ContextLocation.LocalStore;
    public bool HasStorageMarker => NotSavedWithPhoto || InLocalStore;
    /// <summary>The library has a thumbnail stored (false: it is being made).</summary>
    public bool ThumbnailMade => _item.HasThumbnail || _item.Unreadable;
    public const string NotSavedTip = "Rating not saved with the photo yet: neither its folder nor this computer's photo-data folder could be written. Lux tries again by itself.";
    public const string LocalTip = "Saved on this computer: the photo's folder can't be written (a read-only drive or shared folder). The data moves next to the photo when it can.";
    public string StorageMarkerTip => NotSavedWithPhoto ? NotSavedTip : InLocalStore ? LocalTip : "";
    public bool ShowLocalMarker => InLocalStore && !NotSavedWithPhoto;
    public string StorageText => NotSavedWithPhoto ? "Not saved with the photo yet"
        : _item.ContextLocation switch
        {
            ContextLocation.BesideCapture => "With the photo",
            ContextLocation.LocalStore => "On this computer",
            _ => "Nothing saved yet",
        };
    public bool HasSuperseded => !string.IsNullOrEmpty(_item.SupersededContextPath);
    public string AdjustmentsText => _item.Edits.AdjustmentsSummary is { Length: > 0 } a ? char.ToUpperInvariant(a[0]) + a[1..] : "None";

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    /// <summary>The keyboard cursor of the browse view.</summary>
    public bool IsCursor { get => _isCursor; set => SetProperty(ref _isCursor, value); }
    /// <summary>Bumped when a new thumbnail should be fetched.</summary>
    public int ThumbVersion { get => _thumbVersion; set => SetProperty(ref _thumbVersion, value); }

    public string Name => _item.Name;
    public string ShortName => Text.ShortName(_item.Name);
    public int Rating => _item.Rating;
    public string Stars => Text.Stars(_item.Rating);
    public string StarsOutOfFive => Text.StarsOutOfFive(_item.Rating);
    public string RatingLabel => _item.Rating == 0 ? "Not rated" : $"{_item.Rating} of 5 stars";
    public bool Missing => _item.Missing;
    public bool Unreadable => _item.Unreadable;
    public bool HasProblem => _item.Missing || _item.Unreadable;
    public string ProblemText => _item.Missing ? "Missing" : _item.Unreadable ? "Can't read" : "";
    /// <summary>The warning line of the details panel. For a missing photo, it says what happened and what to do. Otherwise it shows the error
    /// that the library keeps for the photo.</summary>
    public string? ProblemNote => _item.Missing
        ? "The file of this photo cannot be found: it was moved or deleted, or its drive is not connected. Connect the drive, or rescan its folder (Settings → Folders)."
        : _item.Error;
    public bool IsFull => _item.CacheLevel == CacheLevel.Full;

    JobState? ActiveState => _queue.ActiveStateOf(_item.Id);
    public bool IsBusy => ActiveState is not null;

    /// <summary>Grid badge (bottom left): edited / Lumen / stack ×4 / focal length.</summary>
    public string Badge => _item.Edits.Source switch
    {
        EditSource.Lux => "edited",
        EditSource.Lumen => "Lumen",
        _ => _item.IsStacked ? $"stack ×{_item.StackFrames}" : Text.Lens(_item.FocalLengthMm),
    };

    /// <summary>Grid corner tag: FULL / 13 / GEO / ···.</summary>
    public string CacheTag => ActiveState is not null && _item.CacheLevel < CacheLevel.Linear13 ? "···" : Text.CacheTag(_item.CacheLevel);

    /// <summary>Timeline card: FULL / 13 MP / queued / analysing / none.</summary>
    public string CacheWord => ActiveState switch
    {
        JobState.Running when _queue.ActiveKindOf(_item.Id) == JobKind.Analysis => "analysing",
        JobState.Running when _queue.ActiveKindOf(_item.Id) == JobKind.CacheUpgrade => "upgrading",
        JobState.Waiting or JobState.Paused when _item.DepthState != DepthState.Analysed => "queued",
        _ => Text.CacheWord(_item.CacheLevel),
    };

    /// <summary>Timeline card meta: "28 mm · edited", "35 mm · stack ×4", "50 mm · Lumen".</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string> { Text.Lens(_item.FocalLengthMm) };
            if (_item.IsStacked) parts.Add($"stack ×{_item.StackFrames}");
            if (_item.Edits.Source == EditSource.Lux) parts.Add("edited");
            else if (_item.Edits.Source == EditSource.Lumen) parts.Add("Lumen");
            if (_item.Missing) parts.Add("missing");
            return string.Join(" · ", parts);
        }
    }

    public string Captured => Text.ShortDateTime(_item.CaptureTime);
    public string CapturedLong => Text.LongDateTime(_item.CaptureTime);
    public string Lens => Text.Lens(_item.FocalLengthMm);
    /// <summary>The list column: "1/640 · ISO 100", "1/15 · ISO 800 ×4" (the details panel shows the library's full text).</summary>
    public string Exposure
    {
        get
        {
            var p = new List<string>();
            if (_item.ExposureTimeSeconds is double t && t > 0)
                p.Add(t >= 0.3 ? t.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s" : "1/" + Math.Round(1 / t).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (_item.Iso is int iso) p.Add("ISO " + iso.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string s = string.Join(" · ", p);
            if (_item.IsStacked) s = (s.Length > 0 ? s + " " : "") + $"×{_item.StackFrames}";
            return s.Length > 0 ? s : "—";
        }
    }

    public string DepthText => _item.Unreadable ? "Can't read" : _item.Missing ? "Missing" : ActiveState switch
    {
        JobState.Running when _queue.ActiveKindOf(_item.Id) == JobKind.Analysis => "Analysing",
        JobState.Waiting or JobState.Paused when _item.DepthState != DepthState.Analysed => "Queued",
        _ => _item.DepthState switch
        {
            DepthState.Analysed => _item.Edits.Source == EditSource.Lumen ? "From Lumen" : "Ready",
            DepthState.Failed => "Failed",
            _ => "Not analysed",
        },
    };

    public string EditsText => Text.Edits(_item.Edits);
    public string DepthEditsDisplay => _item.Edits.IsEdited ? Text.Edits(_item.Edits) + (_item.Edits.HistoryUnreliable ? " · check history" : "") : "None";
    public string CachedRender => Text.CachedRender(_item.CacheLevel, _item.CacheBytes);

    /// <summary>Details line: "12 Oct 2026 16:42 · 28 mm · ISO 100 · 1/640".</summary>
    public string DetailsLine
    {
        get
        {
            var parts = new List<string> { Text.LongDateTime(_item.CaptureTime), Text.Lens(_item.FocalLengthMm) };
            if (_item.ExposureText is { } exp) parts.Add(exp);
            return string.Join(" · ", parts);
        }
    }

    public string DepthEffectText
    {
        get
        {
            var e = _item.Edits;
            if (e.SavedFNumber is not float f || (_item.AllInFocusFNumber > 0 && f >= _item.AllInFocusFNumber)) return "Off";
            return e.SavedFocusMm is float mm && mm > 0 ? $"{Text.FNumber(f)} · {Text.Metres(mm / 1000)}" : $"{Text.FNumber(f)} · default focus";
        }
    }

    public string AccessibleName => $"{_item.Name}, {RatingLabel}{(_item.Missing ? ", missing" : "")}";
}
