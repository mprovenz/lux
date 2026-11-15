// The animated exports: what the app's "Animate…" panel binds to. The effects and their frames are the engine's
// (Lux.Engine.Session.AnimationRenderer); containers are Lux's own GIF / APNG / WebP writers, or ffmpeg for MP4 / WebM when it is installed.

using Lux.Engine.Pipeline.Export;
using Lux.Engine.Session;

namespace Lux.Library;

/// <summary>The file an animation is written to.</summary>
public enum AnimationFormat
{
    /// <summary>GIF: plays everywhere; 256 colours per frame (dithered).</summary>
    Gif = 0,
    /// <summary>Animated PNG: exact colours, larger files; plays in browsers.</summary>
    Apng = 1,
    /// <summary>Animated WebP, lossless: exact colours, smaller than APNG; plays in browsers.</summary>
    WebP = 2,
    /// <summary>MP4 video (H.264) — needs ffmpeg installed.</summary>
    Mp4 = 3,
    /// <summary>WebM video (VP9) — needs ffmpeg installed.</summary>
    WebM = 4,
}

/// <summary>An animated export, as the panel sets it up. Every value has a sensible default; the effect's own values are used by that effect
/// only.</summary>
public sealed record AnimationOptions
{
    public AnimationEffect Effect { get; init; } = AnimationEffect.RackFocus;
    /// <summary>Distinct frames (rack focus, aperture sweep, parallax); the wigglegram and lens tour have one per lens.</summary>
    public int Frames { get; init; } = 24;
    /// <summary>Time per frame, ms; 0 = the effect's default.</summary>
    public int FrameMs { get; init; }
    /// <summary>Play forward then back; null = the effect's default.</summary>
    public bool? PingPong { get; init; }
    /// <summary>Times it plays; 0 = forever.</summary>
    public int Repeat { get; init; }
    /// <summary>Long edge of the file's frames, px.</summary>
    public int LongEdge { get; init; } = 1040;
    public AnimationFormat Format { get; init; } = AnimationFormat.Gif;
    public GifDither GifDither { get; init; } = GifDither.Ordered;
    public GifPalette GifPalette { get; init; } = GifPalette.Global;
    /// <summary>Rack focus: the aperture; aperture sweep: the widest aperture.</summary>
    public float FNumber { get; init; } = 2.0f;
    /// <summary>Aperture sweep start; null = all in focus.</summary>
    public float? FNumberFrom { get; init; }
    /// <summary>Rack focus start / end, mm; null = the nearer / farther parts of the scene.</summary>
    public float? FocusFromMm { get; init; }
    public float? FocusToMm { get; init; }
    /// <summary>Aperture sweep focus, mm; null = Lumen's default focus.</summary>
    public float? FocusMm { get; init; }
    public AnimationPath Path { get; init; } = AnimationPath.Sweep;
    /// <summary>Parallax: how far the virtual camera travels, mm end to end.</summary>
    public double AmplitudeMm { get; init; } = 71.49;
    /// <summary>Parallax: from the main view only (no help from the other wide lenses).</summary>
    public bool SingleView { get; init; }
    public bool UseSavedEdits { get; init; } = true;
    public string? EditsVariant { get; init; }
    /// <summary>Output folder; null = the export folder setting.</summary>
    public string? OutputFolder { get; init; }
    /// <summary>File name stem: <c>{name}</c>, <c>{effect}</c>, <c>{date}</c>, <c>{time}</c>.</summary>
    public string FileNamePattern { get; init; } = "{name}_{effect}";
    public ExistingFilePolicy IfExists { get; init; } = ExistingFilePolicy.Rename;
    /// <summary>As <see cref="ExportOptions.Unturned"/>: false = upright as shown, true = as the camera recorded it, null = the setting.</summary>
    public bool? Unturned { get; init; }

    /// <summary>The engine's description of the same animation.</summary>
    public AnimationSpec ToSpec(int? level = null, int? longEdge = null, int lensIspLevel = 0) => new()
    {
        Effect = Effect, Frames = Frames, FrameMs = FrameMs, PingPong = PingPong, LongEdge = longEdge ?? LongEdge, Level = level,
        FNumber = FNumber, FNumberFrom = FNumberFrom, FocusFromMm = FocusFromMm, FocusToMm = FocusToMm, FocusMm = FocusMm,
        Path = Path, BaselineMm = AmplitudeMm, SingleView = SingleView, UseSavedEdits = UseSavedEdits, EditsVariant = EditsVariant,
        LensIspLevel = lensIspLevel,
    };

    public static string Extension(AnimationFormat f) => f switch
    {
        AnimationFormat.Gif => ".gif", AnimationFormat.Apng => ".png", AnimationFormat.WebP => ".webp", AnimationFormat.Mp4 => ".mp4", _ => ".webm",
    };
    public static bool IsVideo(AnimationFormat f) => f is AnimationFormat.Mp4 or AnimationFormat.WebM;
}

/// <summary>An effect for a capture: whether it can be made and why not.</summary>
public sealed record AnimationEffectInfo(AnimationEffect Effect, string Name, string Description, bool Available, string? Reason, string? Note);

/// <summary>The effects, their plain names and descriptions, and which a capture qualifies for (from its library facts; no file is read).</summary>
public static class AnimationEffects
{
    public static string NameOf(AnimationEffect e) => e switch
    {
        AnimationEffect.Wigglegram => "Wigglegram",
        AnimationEffect.ParallaxSweep => "Parallax",
        AnimationEffect.RackFocus => "Rack focus",
        AnimationEffect.ApertureSweep => "Aperture sweep",
        _ => "Lens tour",
    };

    public static string Key(AnimationEffect e) => e switch
    {
        AnimationEffect.Wigglegram => "wigglegram", AnimationEffect.ParallaxSweep => "parallax", AnimationEffect.RackFocus => "rack-focus",
        AnimationEffect.ApertureSweep => "aperture-sweep", _ => "lens-tour",
    };

    public static string DescriptionOf(AnimationEffect e) => e switch
    {
        AnimationEffect.Wigglegram => "The wide lenses' own pictures one after another: the scene rocks with real depth.",
        AnimationEffect.ParallaxSweep => "A small camera move drawn from the photo and its depth map.",
        AnimationEffect.RackFocus => "The focus moves from one distance to another, with the depth effect.",
        AnimationEffect.ApertureSweep => "The background melts away as the aperture opens from all in focus.",
        _ => "Every lens of the photo in turn: wide, 70 mm and 150 mm views.",
    };

    public static readonly AnimationEffect[] All =
        { AnimationEffect.RackFocus, AnimationEffect.ApertureSweep, AnimationEffect.Wigglegram, AnimationEffect.ParallaxSweep, AnimationEffect.LensTour };

    /// <summary>What a capture can make, from its library facts.</summary>
    public static IReadOnlyList<AnimationEffectInfo> For(CaptureItem item)
    {
        string? noDepth = item.Missing ? "The photo's file cannot be found." : item.Unreadable ? "The photo cannot be read."
                        : item.DepthState == DepthState.Failed ? "This photo's depth map could not be computed." : null;
        // the depth is at hand when the analysis is kept or the active version brings its own (a Lumen import, Lux depth edits)
        string firstAnalysis = item.CacheLevel == CacheLevel.None && item.Edits.Variant is null ? "The depth map is computed first (about a minute)." : "";
        var list = new List<AnimationEffectInfo>();
        foreach (var e in All)
        {
            string? reason = null, note = null;
            switch (e)
            {
                case AnimationEffect.Wigglegram:
                    if (item.IsTelephoto) reason = $"This photo was taken with the {item.FocalLengthMm} mm lenses only: they look at different parts of the scene, so there are no side-by-side views to wiggle between. Wigglegrams need a wide (28 mm) photo.";
                    else note = "Made from the wide lenses' own pictures: tone and depth edits do not apply.";
                    if (item.Missing || item.Unreadable) reason = noDepth;
                    break;
                case AnimationEffect.LensTour:
                    if (item.Missing || item.Unreadable) reason = noDepth;
                    note = $"{item.ModuleCount} lenses.";
                    break;
                case AnimationEffect.ParallaxSweep:
                    reason = noDepth;
                    note = (item.IsTelephoto ? "Telephoto photo: drawn from one view, so keep the movement small. " : "") + firstAnalysis;
                    break;
                default:
                    reason = noDepth;
                    note = firstAnalysis.Length > 0 ? firstAnalysis : null;
                    break;
            }
            list.Add(new AnimationEffectInfo(e, NameOf(e), DescriptionOf(e), reason is null, reason, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        }
        return list;
    }
}

/// <summary>A preview frame (RGBA8, row-major).</summary>
public sealed record PreviewFrame(int Index, int Width, int Height, byte[] Rgba, string Label);

/// <summary>Size and time an animated export is expected to take.</summary>
/// <param name="Basis">How the size was estimated ("from the preview frames", "typical size").</param>
public sealed record AnimationEstimate(long Bytes, TimeSpan Time, int Width, int Height, int PlayedFrames, string Basis);

/// <summary>
/// One capture opened for animated effects (<see cref="ILibrary.OpenAnimationAsync"/>). Thread-safe; nothing blocks the caller: renders run
/// on the thread pool. Dispose when the panel closes (the preview frames and the capture's render state go with it).
/// </summary>
public interface IAnimationSource : IDisposable
{
    string CaptureId { get; }
    CaptureItem Item { get; }
    /// <summary>What this capture can make (header facts: the lens groups), with plain reasons.</summary>
    IReadOnlyList<AnimationEffectInfo> Effects { get; }
    /// <summary>The scene depth (mm) at a point of a preview frame of a depth-effect animation (normalised to the frame, 0,0 = top left) —
    /// picking a focus distance on the photo. Computes the depth map first when needed.</summary>
    Task<float> DepthAtAsync(double x, double y, CancellationToken cancel = default);
    /// <summary>The default rack: the nearer and farther parts of the scene (10th / 90th percentile of the depth), mm; and Lumen's default
    /// focus.</summary>
    Task<(float Near, float Far, float DefaultFocus)> FocusDefaultsAsync(CancellationToken cancel = default);
    /// <summary>The all-in-focus f-number (the aperture sweep's start).</summary>
    float AllInFocusFNumber { get; }
    /// <summary>A progressive preview of <paramref name="options"/> at a small size (export level 4), then for the depth effects and the
    /// parallax sharper frames from export level 3 that replace the quick ones (<see cref="IAnimationPreview.IsSharpening"/>): frames are made
    /// in an order that covers the whole animation early, and kept for this source — asking again for the same frames returns them at once. A new preview of other
    /// options does not stop an earlier one's frames being kept; the earlier one stops rendering.</summary>
    IAnimationPreview Preview(AnimationOptions options);
    /// <summary>The file size and time of the export, from the preview's frames when it has them (encoded in the chosen format and scaled),
    /// else typical figures; the time from the photo's kept data (cache level). Runs off the caller's thread.</summary>
    Task<AnimationEstimate> EstimateAsync(AnimationOptions options, CancellationToken cancel = default);
    /// <summary>The capture's depth was computed (by a Queue job) for this source: its facts (<see cref="Item"/>) are current again.</summary>
    event EventHandler? AnalysisFinished;
    /// <summary>The photo's saved settings changed (e.g. a turn kept for exports): frames made so far are dropped and the settings read again.</summary>
    void Reload();
}

/// <summary>A preview being made (<see cref="IAnimationSource.Preview"/>).</summary>
public interface IAnimationPreview : IDisposable
{
    AnimationOptions Options { get; }
    /// <summary>The plan is known: <see cref="FrameCount"/>, <see cref="Order"/>, <see cref="DelaysMs"/>.</summary>
    bool IsPlanned { get; }
    int FrameCount { get; }
    /// <summary>The played sequence (indices of distinct frames) and each one's time, ms.</summary>
    IReadOnlyList<int> Order { get; }
    IReadOnlyList<int> DelaysMs { get; }
    /// <summary>Frames ready / all distinct frames, 0–1.</summary>
    double Progress { get; }
    /// <summary>What it is doing ("Computing depth", "Rendering frames 3 of 24").</summary>
    string Status { get; }
    bool IsComplete { get; }
    /// <summary>Every frame is ready and sharper frames are being made to replace them (frames at the file's size, up to export level 3).</summary>
    bool IsSharpening => false;
    /// <summary>Why it stopped (plain language); null while fine.</summary>
    string? Error { get; }
    IReadOnlyList<string> Notes { get; }
    /// <summary>A distinct frame when ready, else null.</summary>
    PreviewFrame? Frame(int index);
    /// <summary>The nearest ready frame to <paramref name="index"/> (the player shows it until the exact one arrives); null when none is ready.</summary>
    PreviewFrame? NearestFrame(int index);
    /// <summary>Plan known, a frame ready, finished or failed (any thread).</summary>
    event EventHandler? Changed;
}

/// <summary>Helpers shared by the preview implementations (the library's and a design-time one).</summary>
public static class AnimationPreviews
{
    /// <summary>The values that decide a preview's frames (not their timing or order): previews with the same key share frames.</summary>
    public static string FrameKey(AnimationOptions o) => Animation.AnimationSourceImpl.FrameKey(o);
    /// <summary>First, last, middle, then the middles of what is left: the order frames are made in, so the whole animation is covered early.</summary>
    public static List<int> CoarseOrder(int n) => Animation.PreviewImpl.CoarseOrder(n);
    /// <summary>The preview's notes: the plan's own, then plain reasons the frames change nothing or little (a focus that does not move, an
    /// aperture not below the all-in-focus one, end frames alike) — what <see cref="IAnimationPreview.Notes"/> reports.</summary>
    public static IReadOnlyList<string> Advice(AnimationOptions o, Lux.Engine.Session.AnimationPlan plan, float allInFocus, PreviewFrame? first, PreviewFrame? last)
        => Animation.PreviewImpl.Advice(o, plan, allInFocus, first, last);
}
