using Lux.Engine.Session;
using Lux.Library;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>
/// Design-time animations: the fake capture's scene drawn at 520 × 390 and changed per frame the way the effect changes a real photo —
/// the focus moving between the ground (near) and the sky (far), the background blurring as the aperture opens, the ground sliding against
/// the sky for the wigglegram and the parallax, zoomed views for the lens tour. Frames arrive one at a time (every
/// <see cref="FakeLibrary.FakeFrameMs"/>) in the same coarse-to-fine order as the real preview.
/// </summary>
internal sealed class FakeAnimationSource(FakeLibrary lib, CaptureItem item) : IAnimationSource
{
#pragma warning disable CS0067 // the fake's captures need no analysis for a preview
    public event EventHandler? AnalysisFinished;
#pragma warning restore CS0067
    public void Reload()
    {
        lock (_stores) { _current?.Stop(); _current = null; _stores.Clear(); }
        item = lib.Get(item.Id) ?? item;
    }

    readonly Dictionary<string, Dictionary<int, PreviewFrame>> _stores = new();
    FakePreview? _current;

    public string CaptureId => item.Id;
    public CaptureItem Item => item;
    public IReadOnlyList<AnimationEffectInfo> Effects { get; } = AnimationEffects.For(item);
    public float AllInFocusFNumber => item.AllInFocusFNumber > 0 ? item.AllInFocusFNumber : 15.2f;

    /// <summary>Depth of the fake scene: far at the top of the upright picture (10 m), near at its bottom (1 m); a frame of a photo held
    /// turned is stored sideways, so its point is turned upright first.</summary>
    public Task<float> DepthAtAsync(double x, double y, CancellationToken cancel = default)
    {
        // the frames are upright (exports follow the view shown as held) unless the preview is as captured: then a point of the sideways frame
        // maps to the upright scene by the held turn
        bool unturned = _current?.Options.Unturned ?? lib.Settings.ExportUnturned;
        int turn = item.Orientation == PhotoOrientation.Normal && unturned ? item.DisplayRotation : 0;
        var (_, v) = Lux.App.Controls.AnimationFrameView.FrameToShown(x, y, turn);
        return Task.FromResult((float)(10000 - 9000 * Math.Clamp(v, 0, 1)));
    }
    public Task<(float Near, float Far, float DefaultFocus)> FocusDefaultsAsync(CancellationToken cancel = default) => Task.FromResult((1500f, 8500f, 3000f));

    public IAnimationPreview Preview(AnimationOptions options)
    {
        string key = AnimationPreviews.FrameKey(options);
        lock (_stores)
        {
            if (!_stores.TryGetValue(key, out var store)) _stores[key] = store = new Dictionary<int, PreviewFrame>();
            _current?.Stop();
            _current = new FakePreview(lib, item, options, store);
        }
        _current.Start();
        return _current;
    }

    public Task<AnimationEstimate> EstimateAsync(AnimationOptions o, CancellationToken cancel = default)
    {
        int n = o.Effect switch { AnimationEffect.Wigglegram => 4, AnimationEffect.LensTour => Math.Max(1, item.ModuleCount), _ => o.Frames };
        bool ping = o.PingPong ?? AnimationSpec.DefaultPingPong(o.Effect, o.Path);
        int played = ping && n >= 3 ? 2 * n - 2 : n;
        int w = o.LongEdge, h = (int)Math.Round(o.LongEdge * 0.75);
        if (item.DisplayRotation is 90 or 270 && !(o.Unturned ?? lib.Settings.ExportUnturned)) (w, h) = (h, w);   // upright as shown
        double bpp = o.Format switch { AnimationFormat.Gif => 0.45, AnimationFormat.Apng => 1.6, AnimationFormat.WebP => 1.3, AnimationFormat.Mp4 => 0.03, _ => 0.025 };
        double secs = (item.CacheLevel == CacheLevel.None ? 50 : 6) + n * (0.3 + 1.5 * w * h / 1e6);
        return Task.FromResult(new AnimationEstimate((long)(bpp * w * h * played), TimeSpan.FromSeconds(secs), w, h, played, "from the preview"));
    }

    public void Dispose() { lock (_stores) { _current?.Stop(); _current = null; } }
}

internal sealed class FakePreview : IAnimationPreview
{
    readonly FakeLibrary _lib; readonly CaptureItem _item; readonly Dictionary<int, PreviewFrame> _store;
    readonly CancellationTokenSource _cts = new();
    int _n; List<int> _order = new(); List<int> _delays = new(); List<string> _labels = new();
    volatile bool _planned, _complete; volatile string _status = "Starting";

    public FakePreview(FakeLibrary lib, CaptureItem item, AnimationOptions o, Dictionary<int, PreviewFrame> store) { _lib = lib; _item = item; Options = o; _store = store; }

    public AnimationOptions Options { get; }
    public bool IsPlanned => _planned;
    public int FrameCount => _n;
    public IReadOnlyList<int> Order => _order;
    public IReadOnlyList<int> DelaysMs => _delays;
    public double Progress { get { lock (_store) return _n == 0 ? 0 : Enumerable.Range(0, _n).Count(_store.ContainsKey) / (double)_n; } }
    public string Status => _status;
    public bool IsComplete => _complete;
    public string? Error => null;
    volatile IReadOnlyList<string> _notes = Array.Empty<string>();
    AnimationPlan? _plan;
    /// <summary>The same notes as the real preview (<see cref="AnimationPreviews.Advice"/>) from the fake plan and end frames.</summary>
    public IReadOnlyList<string> Notes => _notes;
    public event EventHandler? Changed;
    void Advise() { if (_plan is { } p) _notes = AnimationPreviews.Advice(Options, p, 15.2f, Frame(0), Frame(_n - 1)); }

    public PreviewFrame? Frame(int index) { lock (_store) return _store.TryGetValue(index, out var f) ? f : null; }
    public PreviewFrame? NearestFrame(int index)
    {
        lock (_store)
            for (int d = 0; d < Math.Max(1, _n); d++)
            {
                if (_store.TryGetValue(index - d, out var a)) return a;
                if (_store.TryGetValue(index + d, out var b)) return b;
            }
        return null;
    }

    public void Start() => _ = Task.Run(RunAsync);
    public void Stop() => _cts.Cancel();
    public void Dispose() => Stop();
    void Raise() { try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { } }

    async Task RunAsync()
    {
        var o = Options; var ct = _cts.Token;
        try
        {
            string[] lenses = { "A1", "A2", "A3", "A4", "A5", "B1", "B2", "B3", "B4", "B5", "C1" };
            _n = o.Effect switch { AnimationEffect.Wigglegram => 4, AnimationEffect.LensTour => Math.Min(lenses.Length, Math.Max(2, _item.ModuleCount)), _ => Math.Clamp(o.Frames, 2, 240) };
            for (int k = 0; k < _n; k++)
            {
                double t = (double)k / (_n - 1), e = 0.5 - 0.5 * Math.Cos(Math.PI * t);
                float z1 = o.FocusFromMm ?? 1500, z2 = o.FocusToMm ?? 8500;
                _labels.Add(o.Effect switch
                {
                    AnimationEffect.RackFocus => $"f/{o.FNumber:0.#} · focus {1.0 / ((1 - e) / z1 + e / z2) / 1000:0.00} m",
                    AnimationEffect.ApertureSweep => k == 0 ? "all in focus" : $"f/{1.0 / ((1 - e) / 15.2 + e / o.FNumber):0.#}",
                    AnimationEffect.Wigglegram => new[] { "A5", "A1", "A3", "A4" }[k],
                    AnimationEffect.LensTour => lenses[k],
                    _ => $"view {k + 1} of {_n}",
                });
            }
            bool ping = o.PingPong ?? AnimationSpec.DefaultPingPong(o.Effect, o.Path);
            _order = Enumerable.Range(0, _n).ToList();
            if (ping && _n >= 3) for (int i = _n - 2; i >= 1; i--) _order.Add(i);
            _delays = Enumerable.Repeat(o.FrameMs > 0 ? o.FrameMs : AnimationSpec.DefaultFrameMs(o.Effect), _order.Count).ToList();
            var infos = new List<AnimationFrameInfo>();
            for (int k = 0; k < _n; k++)
            {
                double t = (double)k / (_n - 1), e = 0.5 - 0.5 * Math.Cos(Math.PI * t);
                float z1 = o.FocusFromMm ?? 1500, z2 = o.FocusToMm ?? 8500;
                infos.Add(o.Effect switch
                {
                    AnimationEffect.RackFocus => new AnimationFrameInfo(k, _labels[k]) { FNumber = o.FNumber, FocusMm = (float)Math.Round(1.0 / ((1 - e) / z1 + e / z2), 1) },
                    AnimationEffect.ApertureSweep => new AnimationFrameInfo(k, _labels[k]) { FNumber = k == 0 || o.FNumber >= 15.2f ? null : (float)(1.0 / ((1 - e) / 15.2 + e / o.FNumber)) },
                    _ => new AnimationFrameInfo(k, _labels[k]),
                });
            }
            _plan = new AnimationPlan { Effect = o.Effect, Frames = infos, Order = _order, DelaysMs = _delays, Size = (W, H) };
            Advise();
            _planned = true; Raise();
            var order = AnimationPreviews.CoarseOrder(_n);
            int done = 0;
            foreach (var k in order)
            {
                ct.ThrowIfCancellationRequested();
                if (Frame(k) != null) { done++; continue; }
                _status = $"Preparing frames · {done} of {_n}"; Raise();
                if (_lib.FakeFrameMs > 0) await Task.Delay(_lib.FakeFrameMs, ct).ConfigureAwait(false);
                var f = Paint(k);
                lock (_store) _store[k] = f;
                done++;
                if (k == 0 || k == _n - 1) Advise();
                Raise();
            }
            Advise();
            _status = $"{_n} frames ready"; _complete = true; Raise();
        }
        catch (OperationCanceledException) { _status = "Stopped"; }
    }

    const int W = 520, H = 390;

    /// <summary>A frame as the capture records it: the scene upright (W × H), or — for a photo held turned whose turn is not saved — the
    /// upright portrait scene stored sideways in the landscape frame, as the camera stores it (the player turns it back for display).</summary>
    PreviewFrame Paint(int k)
    {
        var o = Options;
        double t = _n <= 1 ? 0 : (double)k / (_n - 1), e = 0.5 - 0.5 * Math.Cos(Math.PI * t);
        int index = Math.Abs(_item.Id.GetHashCode()) % 97;
        int held = _item.HeldOrientation switch { PhotoOrientation.Rotate90 => 90, PhotoOrientation.Rotate180 => 180, PhotoOrientation.Rotate270 => 270, _ => 0 };
        if (_item.DisplayRotation != 0) held = _item.DisplayRotation;
        // exports (and so animations) follow the view shown as held unless asked for as captured: the frames are upright
        // then; as captured, or with the held view off, the portrait scene lies sideways in the frame as the camera stores it
        bool unturned = o.Unturned ?? _lib.Settings.ExportUnturned;
        bool sideways = held != 0 && _item.Orientation == PhotoOrientation.Normal && (_item.DisplayRotation == 0 || unturned);
        bool portrait = held is 90 or 270 && _item.Orientation == PhotoOrientation.Normal && (sideways || _item.DisplayRotation != 0);
        int pw = portrait ? H : W, ph = portrait ? W : H;   // the upright picture's size
        using var scene = new SKBitmap(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(scene)) { c.Scale(pw / 400f, ph / 300f); FakeThumbnails.DrawScene(c, index, _item.IsStacked, _item.IsTelephoto, 400, 300); }
        using var upright = new SKBitmap(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(upright))
        {
            c.Clear(SKColors.Black);
            float horizon = ph * 0.55f;
            void Layer(SKRect clip, float blur, float dx, float scale = 1)
            {
                c.Save();
                c.ClipRect(clip);
                using var p = new SKPaint { ImageFilter = blur > 0.3f ? SKImageFilter.CreateBlur(blur, blur) : null };
                c.Translate(pw / 2f + dx, ph / 2f); c.Scale(scale); c.Translate(-pw / 2f, -ph / 2f);
                c.DrawBitmap(scene, 0, 0, p);
                c.Restore();
            }
            var sky = new SKRect(0, 0, pw, horizon); var ground = new SKRect(0, horizon, pw, ph);
            switch (o.Effect)
            {
                case AnimationEffect.RackFocus:
                    Layer(sky, (float)(8 * (1 - e)), 0); Layer(ground, (float)(8 * e), 0); break;
                case AnimationEffect.ApertureSweep:
                    Layer(sky, (float)(10 * e), 0); Layer(ground, (float)(2 * e), 0); break;
                case AnimationEffect.Wigglegram:
                case AnimationEffect.ParallaxSweep:
                {
                    double s = Math.Sin(2 * Math.PI * t) * (o.Effect == AnimationEffect.Wigglegram ? 6 : 6 * o.AmplitudeMm / 71.49);
                    Layer(sky, 0, (float)(-s * 0.3)); Layer(ground, 0, (float)s); break;
                }
                default:
                    Layer(new SKRect(0, 0, pw, ph), 0, 0, k < 5 ? 1f : k < 10 ? 2.5f : 5f); break;
            }
        }
        // as recorded (sideways): the landscape frame with the upright picture turned back by the held angle; upright: the picture itself
        int ow = sideways ? W : pw, oh = sideways ? H : ph;
        using var outBmp = new SKBitmap(ow, oh, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(outBmp))
        {
            c.Clear(SKColors.Black);
            c.Translate(ow / 2f, oh / 2f);
            if (sideways) c.RotateDegrees(-held);
            c.DrawBitmap(upright, -pw / 2f, -ph / 2f);
        }
        var px = outBmp.Bytes;
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return new PreviewFrame(k, ow, oh, px, _labels[k]);
    }
}
