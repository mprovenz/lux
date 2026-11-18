using Lux.Engine.Session;
using Lux.Library;
using SkiaSharp;

namespace Lux.App.Fake;

/// <summary>
/// Design-time animations: the fake capture's scene drawn at 520 × 390 and changed per frame the way the effect changes a real photo —
/// the focus moving between the ground (near) and the sky (far), the background blurring as the aperture opens, the ground sliding against
/// the sky for the wigglegram and the parallax, zoomed views for the lens tour. Frames arrive one at a time (every
/// <see cref="FakeLibrary.FakeFrameMs"/>) in the same coarse-to-fine order as the real preview. With <see cref="FakeLibrary.FakeSharpPass"/>
/// the effects that have a second pass in the real library make sharper frames (1040 × 780) after the quick ones.
/// </summary>
internal sealed class FakeAnimationSource(FakeLibrary lib, CaptureItem item) : IAnimationSource
{
#pragma warning disable CS0067 // the fake's captures need no analysis for a preview
    public event EventHandler? AnalysisFinished;
#pragma warning restore CS0067
    public void Reload()
    {
        lock (_stores) { _current?.Stop(); _current = null; _stores.Clear(); _sharpStores.Clear(); }
        item = lib.Get(item.Id) ?? item;
    }

    readonly Dictionary<string, Dictionary<int, PreviewFrame>> _stores = new();
    readonly Dictionary<string, Dictionary<int, PreviewFrame>> _sharpStores = new();
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
            if (!_sharpStores.TryGetValue(key, out var sharp)) _sharpStores[key] = sharp = new Dictionary<int, PreviewFrame>();
            _current?.Stop();
            _current = new FakePreview(lib, item, options, store, sharp);
        }
        _current.Start();
        return _current;
    }

    public Task<AnimationEstimate> EstimateAsync(AnimationOptions o, CancellationToken cancel = default)
    {
        int n = o.Effect switch { AnimationEffect.Wigglegram => 4, AnimationEffect.LensTour => Math.Max(1, item.ModuleCount), _ => o.Frames };
        bool ping = o.PingPong ?? AnimationSpec.DefaultPingPong(o.Effect, o.Path);
        int played = ping && n >= 3 ? 2 * n - 2 : n;
        if (!ping && AnimationSpec.PrefersVideo(o.Effect)) played += (o.HoldMs ?? AnimationSpec.DefaultHoldMs(o.Effect)) / (o.FrameMs > 0 ? o.FrameMs : AnimationSpec.DefaultFrameMs(o.Effect));
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
    readonly FakeLibrary _lib; readonly CaptureItem _item; readonly Dictionary<int, PreviewFrame> _store, _sharp;
    readonly CancellationTokenSource _cts = new();
    int _n; List<int> _order = new(); List<int> _delays = new(); List<string> _labels = new();
    volatile bool _planned, _complete, _sharpening, _final, _sharpSetUp; volatile string _status = "Starting";
    /// <summary>A second pass of sharper frames follows the quick ones (the same effects as in the real library).</summary>
    readonly bool _twoPass;

    public FakePreview(FakeLibrary lib, CaptureItem item, AnimationOptions o, Dictionary<int, PreviewFrame> store, Dictionary<int, PreviewFrame> sharp)
    {
        _lib = lib; _item = item; Options = o; _store = store; _sharp = sharp;
        _twoPass = lib.FakeSharpPass && (o.Effect == AnimationEffect.ZoomThrough
                   || o.Effect is AnimationEffect.RackFocus or AnimationEffect.ApertureSweep or AnimationEffect.ParallaxSweep && o.LongEdge > 600);
        _sharpSetUp = _twoPass;
    }

    public AnimationOptions Options { get; }
    public bool IsPlanned => _planned;
    public int FrameCount => _n;
    public IReadOnlyList<int> Order => _order;
    public IReadOnlyList<int> DelaysMs => _delays;
    public double Progress { get { lock (_store) return _n == 0 ? 0 : Enumerable.Range(0, _n).Count(_store.ContainsKey) / (double)_n; } }
    public string Status => _status;
    public bool IsComplete => _complete;
    public bool IsSharpening => _sharpening;
    public bool IsFinal => _final;
    public PreviewFrame? FinalFrame(int index) { lock (_store) return (_twoPass ? _sharp : _store).TryGetValue(index, out var f) ? f : null; }
    public PreviewWork Work
    {
        get
        {
            if (!_planned || _n == 0) return new PreviewWork(_status, null);
            int quick, sharp;
            lock (_store) { quick = Enumerable.Range(0, _n).Count(_store.ContainsKey); sharp = Enumerable.Range(0, _n).Count(_sharp.ContainsKey); }
            if (!_twoPass) return new PreviewWork($"Frame {quick} of {_n}", (double)quick / _n);
            double fraction = (quick + 4.0 * sharp) / (_n * 5.0);
            if (quick < _n) return new PreviewWork($"Frame {quick} of {_n} · first pass", fraction);
            if (_sharpSetUp && sharp == 0)
                return Options.Effect == AnimationEffect.ZoomThrough ? new PreviewWork("Making the full-size picture. This takes a minute or two.", null)
                                                                     : new PreviewWork("Preparing the final pass", fraction);
            return new PreviewWork($"Frame {sharp} of {_n} · final pass", fraction);
        }
    }
    public string? Error => null;
    volatile IReadOnlyList<string> _notes = Array.Empty<string>();
    AnimationPlan? _plan;
    /// <summary>The same notes as the real preview (<see cref="AnimationPreviews.Advice"/>) from the fake plan and end frames.</summary>
    public IReadOnlyList<string> Notes => _notes;
    public event EventHandler? Changed;
    void Advise() { if (_plan is { } p) _notes = AnimationPreviews.Advice(Options, p, 15.2f, Quick(0), Quick(_n - 1)); }

    /// <summary>The sharpest frame there is.</summary>
    public PreviewFrame? Frame(int index) { lock (_store) return _sharp.TryGetValue(index, out var s) ? s : _store.TryGetValue(index, out var f) ? f : null; }
    PreviewFrame? Quick(int index) { lock (_store) return _store.TryGetValue(index, out var f) ? f : null; }
    public PreviewFrame? NearestFrame(int index)
    {
        for (int d = 0; d < Math.Max(1, _n); d++)
        {
            if (Frame(index - d) is { } a) return a;
            if (Frame(index + d) is { } b) return b;
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
                    AnimationEffect.ZoomThrough => $"zoom {Math.Exp(e * Math.Log(8.0)):0.0}×",
                    AnimationEffect.Fog => $"fog {100 * e:0} %",
                    AnimationEffect.PanZoom => $"zoom {1.10 + 0.25 * e:0.00}×",
                    AnimationEffect.SliceReveal => $"revealed {100 * e:0} %",
                    AnimationEffect.TiltShift => $"f/0.05 · focus {1.0 / ((1 - e) / z1 + e / z2) / 1000:0.0} m",
                    _ => $"view {k + 1} of {_n}",
                });
            }
            bool ping = o.PingPong ?? AnimationSpec.DefaultPingPong(o.Effect, o.Path);
            _order = Enumerable.Range(0, _n).ToList();
            if (ping && _n >= 3) for (int i = _n - 2; i >= 1; i--) _order.Add(i);
            else if (AnimationSpec.PrefersVideo(o.Effect))
            {
                // the last frame stays for the hold time, as in the real plan
                int ms = o.FrameMs > 0 ? o.FrameMs : AnimationSpec.DefaultFrameMs(o.Effect);
                for (int i = (o.HoldMs ?? AnimationSpec.DefaultHoldMs(o.Effect)) / ms; i > 0; i--) _order.Add(_n - 1);
            }
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
                if (Quick(k) != null) { done++; continue; }
                _status = $"Preparing frames · {done} of {_n}"; Raise();
                if (_lib.FakeFrameMs > 0) await Task.Delay(_lib.FakeFrameMs, ct).ConfigureAwait(false);
                var f = Paint(k, 1);
                lock (_store) _store[k] = f;
                done++;
                if (k == 0 || k == _n - 1) Advise();
                Raise();
            }
            Advise();
            _status = $"{_n} frames ready";
            // as the real preview: never "complete" without "sharpening" while a second pass is still to come
            if (_twoPass) _sharpening = true; else _final = true;
            _complete = true; Raise();
            if (_twoPass)
            {
                int sharpDone;
                lock (_store) sharpDone = order.Count(_sharp.ContainsKey);
                _status = $"{_n} frames ready · sharpening {sharpDone} of {_n}"; Raise();
                if (sharpDone < _n && _lib.FakeSharpSetUpMs > 0) await Task.Delay(_lib.FakeSharpSetUpMs, ct).ConfigureAwait(false);
                _sharpSetUp = false;
                foreach (var k in order)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_store) if (_sharp.ContainsKey(k)) continue;
                    _status = $"{_n} frames ready · sharpening {sharpDone} of {_n}"; Raise();
                    if (_lib.FakeFrameMs > 0) await Task.Delay(_lib.FakeFrameMs, ct).ConfigureAwait(false);
                    var f = Paint(k, 2);
                    lock (_store) _sharp[k] = f;
                    sharpDone++;
                    Raise();
                }
                _final = true; _sharpening = false;
                _status = $"{_n} frames ready"; Raise();
            }
        }
        catch (OperationCanceledException) { _status = "Stopped"; }
        finally { _sharpening = false; _sharpSetUp = false; }
    }

    const int W = 520, H = 390;

    /// <summary>A frame as the capture records it: the scene upright (W × H), or — for a photo held turned whose turn is not saved — the
    /// upright portrait scene stored sideways in the landscape frame, as the camera stores it (the player turns it back for display).
    /// <paramref name="scale"/> 2 gives the sharper frame of the second pass: the same picture at twice the size.</summary>
    PreviewFrame Paint(int k, int scale)
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
        // the scene and the picture have scale × the pixels; the drawing below stays in the units of the small picture
        using var scene = new SKBitmap(pw * scale, ph * scale, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(scene)) { c.Scale(pw * scale / 400f, ph * scale / 300f); FakeThumbnails.DrawScene(c, index, _item.IsStacked, _item.IsTelephoto, 400, 300); }
        var whole = SKRect.Create(pw, ph);
        using var upright = new SKBitmap(pw * scale, ph * scale, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(upright))
        {
            c.Clear(SKColors.Black);
            c.Scale(scale);
            float horizon = ph * 0.55f;
            void Layer(SKRect clip, float blur, float dx, float scale = 1)
            {
                c.Save();
                c.ClipRect(clip);
                using var p = new SKPaint { ImageFilter = blur > 0.3f ? SKImageFilter.CreateBlur(blur, blur) : null };
                c.Translate(pw / 2f + dx, ph / 2f); c.Scale(scale); c.Translate(-pw / 2f, -ph / 2f);
                c.DrawBitmap(scene, whole, p);
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
                case AnimationEffect.ZoomThrough:
                {
                    // the whole picture, enlarged around the target (the middle when none is picked)
                    var at = o.Target ?? (0.5, 0.5);
                    float zoom = (float)Math.Exp(e * Math.Log(8.0));
                    c.Save();
                    c.Translate(pw / 2f, ph / 2f); c.Scale(zoom); c.Translate((float)(-pw * (0.5 + (at.X - 0.5) * e)), (float)(-ph * (0.5 + (at.Y - 0.5) * e)));
                    c.DrawBitmap(scene, whole);
                    c.Restore();
                    break;
                }
                case AnimationEffect.Fog:
                {
                    // fog covers the far sky first, then the near ground
                    Layer(new SKRect(0, 0, pw, ph), 0, 0);
                    using var fogPaint = new SKPaint();
                    fogPaint.Color = new SKColor(225, 228, 232, (byte)(230 * e * o.FogDensity / Math.Max(1, o.FogDensity))); c.DrawRect(sky, fogPaint);
                    fogPaint.Color = new SKColor(225, 228, 232, (byte)(110 * e * e)); c.DrawRect(ground, fogPaint);
                    break;
                }
                case AnimationEffect.PanZoom:
                {
                    float s = o.Flat ? 0 : (float)((e - 0.5) * 16 * (o.Travel ?? 0.02) / 0.02), zoom = (float)(1.10 + 0.25 * e);
                    Layer(sky, 0, -s * 0.3f, zoom); Layer(ground, 0, s, zoom); break;
                }
                case AnimationEffect.SliceReveal:
                {
                    // the near ground comes in first, then the far sky; hidden parts are gray and dim
                    void Part(SKRect clip, double shown)
                    {
                        c.Save(); c.ClipRect(clip);
                        float a = (float)Math.Clamp(shown, 0, 1);
                        float[] m = { 0.3f + 0.7f * a, 0.3f * (1 - a), 0.25f * (1 - a), 0, 0, 0.17f * (1 - a), 0.33f + 0.67f * a, 0.25f * (1 - a), 0, 0,
                                      0.17f * (1 - a), 0.3f * (1 - a), 0.08f + 0.92f * a, 0, 0, 0, 0, 0, 1, 0 };
                        using var p = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(m) };
                        c.DrawBitmap(scene, whole, p);
                        c.Restore();
                    }
                    bool nearFirst = o.SliceOrder != Lux.Engine.Pipeline.Animate.SliceOrder.FarToNear;
                    Part(ground, nearFirst ? e * 2 : e * 2 - 1); Part(sky, nearFirst ? e * 2 - 1 : e * 2);
                    break;
                }
                case AnimationEffect.TiltShift:
                    Layer(sky, (float)(12 * (1 - e)), 0); Layer(ground, (float)(12 * e), 0); break;
                default:
                    Layer(new SKRect(0, 0, pw, ph), 0, 0, k < 5 ? 1f : k < 10 ? 2.5f : 5f); break;
            }
        }
        // as recorded (sideways): the landscape frame with the upright picture turned back by the held angle; upright: the picture itself
        int ow = (sideways ? W : pw) * scale, oh = (sideways ? H : ph) * scale;
        using var outBmp = new SKBitmap(ow, oh, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(outBmp))
        {
            c.Clear(SKColors.Black);
            c.Translate(ow / 2f, oh / 2f);
            if (sideways) c.RotateDegrees(-held);
            c.DrawBitmap(upright, -pw * scale / 2f, -ph * scale / 2f);
        }
        var px = outBmp.Bytes;
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return new PreviewFrame(k, ow, oh, px, _labels[k]);
    }
}
