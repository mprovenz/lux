using Lux.App.ViewModels;
using Lux.Library;
using SkiaSharp;

namespace Lux.App.Services;

/// <summary>Makes the camera page's thumbnails from the camera's preview JPEGs (1440 × 1080 to 4160 × 3120, 0.3–4 MB): decoded at a reduced
/// scale (the JPEG decoder's own 1/2 · 1/4 · 1/8 steps), scaled to <see cref="Width"/> pixels wide, turned upright from the JPEG's orientation
/// tag, and written as a small JPEG (≈ 20–40 KB) that the camera service keeps in the app's data folder.</summary>
public static class CameraPreviews
{
    public const int Width = 480;

    public static byte[]? Shrink(byte[] jpeg)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        var scaled = codec.GetScaledDimensions(Math.Min(1f, Width * 2f / info.Width));   // decode at no less than twice the target
        using var decoded = new SKBitmap(new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var r = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        int w = Math.Min(Width, scaled.Width), h = Math.Max(1, (int)Math.Round(scaled.Height * (double)w / scaled.Width));
        using var small = decoded.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
        if (small is null) return null;
        using var upright = Orient(small, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(upright ?? small);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        return encoded?.ToArray();
    }

    /// <summary>The bitmap turned as its EXIF orientation says (null when it is already upright).</summary>
    static SKBitmap? Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return null;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        float w = src.Width, h = src.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, w / 2, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, w / 2, h / 2); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, h / 2); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, h / 2); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, 0); canvas.RotateDegrees(90); canvas.Scale(-1, -1, w / 2, h / 2); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }
}

/// <summary>The camera state in the status bar (<see cref="IDeviceStatus"/>): "No camera connected", "Light L16 connected · 38 new".</summary>
public sealed class CameraDeviceStatus : IDeviceStatus
{
    readonly ICameraService _camera;
    public CameraDeviceStatus(ICameraService camera)
    {
        _camera = camera;
        camera.StatusChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public string StatusText => Describe(_camera.Status);

    public static string Describe(CameraStatus s)
    {
        string name = s.DeviceName ?? "Camera";
        return s.State switch
        {
            CameraState.Unavailable or CameraState.NotConnected => "No camera connected",
            CameraState.Connecting => "Opening the camera",
            CameraState.Busy => "Camera in use by another program",
            CameraState.Error => "The camera could not be read",
            _ when s.IsImporting => $"{name} connected · importing",
            CameraState.Listing when s.CheckingEarlierList => $"{name} connected · checking for changes",
            CameraState.Listing when s.Phase == CameraListingPhase.Details && s.ListTotal > 0 => $"{name} connected · reading the camera ({Text.Number(s.Listed)} of {Text.Number(s.ListTotal)})",
            CameraState.Listing => $"{name} connected · reading the camera",
            _ => s.New > 0 ? $"{name} connected · {Text.Number(s.New)} new" : $"{name} connected · nothing new",
        };
    }
}

/// <summary>No camera support (tests that do not exercise the camera, and the fallback when the service cannot start).</summary>
public sealed class UnavailableCameraService : ICameraService
{
    readonly string _message;
    public UnavailableCameraService(string message = "Importing from the camera is not available in this version.") => _message = message;
    public CameraStatus Status => new() { State = CameraState.Unavailable, Message = _message };
    public event EventHandler? StatusChanged { add { } remove { } }
    public IReadOnlyList<CameraCapture> Captures => Array.Empty<CameraCapture>();
    public event EventHandler<CameraCapturesChangedEventArgs>? CapturesChanged { add { } remove { } }
    public Task<byte[]?> GetPreviewAsync(CameraCapture capture, CancellationToken cancel = default) => Task.FromResult<byte[]?>(null);
    public CameraImportPlan PlanImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options) => new() { Count = captures.Count, Problem = _message };
    public long StartImport(IReadOnlyCollection<CameraCapture> captures, CameraImportOptions options) => throw new LibraryException(_message);
    public CameraImportSummary? LastImport => null;
    public event EventHandler? LastImportChanged { add { } remove { } }
    public (int Analysed, int Total)? LastImportAnalysis() => null;
    public void Reconnect() { }
    public string DefaultDestination => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Light");
    public void Dispose() { }
}
