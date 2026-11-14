using System.Diagnostics;
using System.Text.Json;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// Lux's own animation writers (GIF, APNG, lossless WebP) checked by an independent decoder: every file is decoded with Python Pillow and
/// ImageMagick (a validation script in the workspace) and compared frame by frame, pixel-exactly, with what the writer says a viewer
/// shows (for GIF: the frames after its palette reduction; for APNG and WebP: the input frames), plus delays and the loop count. Skipped
/// (passes with a note) when python3 with Pillow is not available.
/// </summary>
public class AnimationWriterTests
{
    static string Dir => Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "writers");

    /// <summary>Deterministic test frames: a gradient, a moving disc, a still textured block and noise in one corner.</summary>
    internal static List<Rgba> Synthetic(int w, int h, int n, int seed = 1)
    {
        var rnd = new Random(seed);
        var noise = new byte[w * h]; rnd.NextBytes(noise);
        var list = new List<Rgba>();
        for (int f = 0; f < n; f++)
        {
            var img = new Rgba(w, h);
            double cx = w * (0.2 + 0.6 * f / Math.Max(1, n - 1)), cy = h * 0.5, rad = h * 0.18;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long p = ((long)y * w + x) * 4;
                    int r = 40 + 180 * x / w, g = 60 + 150 * y / h, b = 200 - 120 * x / w;
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < rad * rad) { r = 230; g = 120 + f * 3 % 60; b = 40; }
                    if (x < w / 4 && y < h / 4) { int v = noise[y * w + x]; r = v; g = (v * 7) & 255; b = 255 - v; }
                    if (x > w * 3 / 4 && y > h * 3 / 4) { r = g = b = ((x / 4 + y / 4) & 1) * 255; }
                    img.P[p] = (byte)r; img.P[p + 1] = (byte)g; img.P[p + 2] = (byte)b; img.P[p + 3] = 255;
                }
            list.Add(img);
        }
        return list;
    }

    /// <summary>Write the animation, the expected frames and the metadata, then run the validator. Returns its output.</summary>
    internal static (bool Ran, int Exit, string Output, AnimationEncodeResult Result, string Path) WriteAndValidate(string name, AnimationContainer c, IReadOnlyList<Rgba> frames,
        IReadOnlyList<int> delays, AnimationEncodeOptions o, string? dir = null)
    {
        dir ??= Dir;
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + AnimationEncoder.Extension(c));
        var res = AnimationEncoder.WriteFile(path, c, frames, delays, o, keepDisplayedFrames: true);
        string fpath = System.IO.Path.Combine(dir, name + ".frames"), jpath = System.IO.Path.Combine(dir, name + ".json");
        using (var fs = File.Create(fpath)) foreach (var f in res.DisplayedFrames!) fs.Write(f.P);
        File.WriteAllText(jpath, JsonSerializer.Serialize(new
        {
            w = frames[0].W, h = frames[0].H, n = frames.Count, delays_ms = res.WrittenDelaysMs, loops = o.Loops,
            container = c switch { AnimationContainer.Gif => "gif", AnimationContainer.Apng => "apng", _ => "webp" },
        }));
        string script = System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, "..", "tools", "validate_anim.py"));
        if (!File.Exists(script) || !HasPillow()) return (false, 0, "validator not available", res, path);
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-I", script, path, fpath, jpath }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (true, p.ExitCode, outp, res, path);
    }

    static bool? _pillow;
    static bool HasPillow()
    {
        if (_pillow is bool b) return b;
        try
        {
            var psi = new ProcessStartInfo("python3", "-c \"import PIL\"") { RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!; p.WaitForExit();
            return (_pillow = p.ExitCode == 0).Value;
        }
        catch (System.ComponentModel.Win32Exception) { return (_pillow = false).Value; }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var c in new[] { AnimationContainer.Gif, AnimationContainer.Apng, AnimationContainer.WebP })
            foreach (var diff in new[] { true, false })
                yield return new object[] { c, diff };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsThroughAnIndependentDecoder(AnimationContainer c, bool diff)
    {
        var frames = Synthetic(203, 151, 7);
        var delays = new[] { 100, 60, 60, 70, 33, 250, 100 };
        var o = new AnimationEncodeOptions { FrameDifferencing = diff, Loops = 0 };
        var (ran, exit, output, res, _) = WriteAndValidate($"synthetic-{c}-{(diff ? "diff" : "full")}", c, frames, delays, o);
        Assert.True(res.Bytes > 0);
        if (ran) Assert.True(exit == 0, output);
    }

    [Theory]
    [InlineData(GifDither.None, GifPalette.Global)]
    [InlineData(GifDither.Diffusion, GifPalette.Global)]
    [InlineData(GifDither.Ordered, GifPalette.PerFrame)]
    public void GifOptionsDecodeExactly(GifDither d, GifPalette p)
    {
        var frames = Synthetic(160, 120, 5, 3);
        var o = new AnimationEncodeOptions { GifDither = d, GifPalette = p, Loops = 3 };
        var (ran, exit, output, _, _) = WriteAndValidate($"synthetic-gif-{d}-{p}", AnimationContainer.Gif, frames, new[] { 80, 80, 80, 80, 80 }, o);
        if (ran) Assert.True(exit == 0, output);
    }

    [Fact]
    public void LoopCountsAndSingleFrame()
    {
        foreach (var c in new[] { AnimationContainer.Gif, AnimationContainer.Apng, AnimationContainer.WebP })
        {
            var one = Synthetic(64, 48, 1, 9);
            var (ran, exit, output, _, _) = WriteAndValidate($"single-{c}", c, one, new[] { 500 }, new AnimationEncodeOptions { Loops = 1 });
            if (ran) Assert.True(exit == 0, output);
            var two = Synthetic(66, 50, 2, 9);
            (ran, exit, output, _, _) = WriteAndValidate($"loops5-{c}", c, two, new[] { 120, 240 }, new AnimationEncodeOptions { Loops = 5 });
            if (ran) Assert.True(exit == 0, output);
        }
    }

    [Fact]
    public void CancelledEncodeLeavesNoFile()
    {
        Directory.CreateDirectory(Dir);
        string path = System.IO.Path.Combine(Dir, "cancelled.gif");
        File.Delete(path);
        using var cts = new CancellationTokenSource();
        var frames = Synthetic(120, 90, 6);
        var o = new AnimationEncodeOptions { Cancel = cts.Token, Progress = (d, _) => { if (d == 2) cts.Cancel(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => AnimationEncoder.WriteFile(path, AnimationContainer.Gif, frames, Enumerable.Repeat(100, 6).ToArray(), o));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }
}

/// <summary>Measurements of the writers on real frames (env-gated: LUX_M6_FRAMES = a raw .frames file written by
/// `lux-light animate --anim-keep-frames`, its .json beside it). Writes variants into a workspace folder with a timing table.</summary>
public class AnimationWriterMeasurements
{
    [Fact]
    public void Variants()
    {
        var src = Environment.GetEnvironmentVariable("LUX_M6_FRAMES");
        if (string.IsNullOrEmpty(src)) return;
        using var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(src, ".json")));
        int w = meta.RootElement.GetProperty("w").GetInt32(), h = meta.RootElement.GetProperty("h").GetInt32(), n = meta.RootElement.GetProperty("n").GetInt32();
        var raw = File.ReadAllBytes(src);
        var frames = Enumerable.Range(0, n).Select(i => new Rgba(w, h, raw.AsSpan(i * w * h * 4, w * h * 4).ToArray())).ToList();
        var delays = meta.RootElement.GetProperty("delays_ms").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        string dir = Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "writers", "real", Path.GetFileNameWithoutExtension(src));
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        void One(string name, AnimationContainer c, AnimationEncodeOptions o)
        {
            var sw = Stopwatch.StartNew();
            var r = AnimationEncoder.WriteFile(Path.Combine(dir, name + AnimationEncoder.Extension(c)), c, frames, delays, o);
            lines.Add($"{name}\t{r.Bytes}\t{sw.Elapsed.TotalSeconds:F3}");
        }
        foreach (var s in new[] { 6.0, 12, 18, 24, 32 }) One($"gif-ordered-{s}", AnimationContainer.Gif, new AnimationEncodeOptions { GifDitherStrength = s });
        One("gif-none", AnimationContainer.Gif, new AnimationEncodeOptions { GifDither = GifDither.None });
        One("gif-diffusion", AnimationContainer.Gif, new AnimationEncodeOptions { GifDither = GifDither.Diffusion });
        One("gif-perframe", AnimationContainer.Gif, new AnimationEncodeOptions { GifPalette = GifPalette.PerFrame });
        One("gif-nodiff", AnimationContainer.Gif, new AnimationEncodeOptions { FrameDifferencing = false });
        One("apng", AnimationContainer.Apng, new AnimationEncodeOptions());
        One("apng-optimal", AnimationContainer.Apng, new AnimationEncodeOptions { PngCompression = System.IO.Compression.CompressionLevel.Optimal });
        One("apng-nodiff", AnimationContainer.Apng, new AnimationEncodeOptions { FrameDifferencing = false });
        foreach (var e in new[] { 1, 5, 9 }) One($"webp-e{e}", AnimationContainer.WebP, new AnimationEncodeOptions { WebPEffort = e });
        One("webp-nodiff", AnimationContainer.WebP, new AnimationEncodeOptions { FrameDifferencing = false });
        File.WriteAllLines(Path.Combine(dir, "sizes.tsv"), new[] { $"raw\t{(long)w * h * 3 * n}\t0" }.Concat(lines));
    }
}
