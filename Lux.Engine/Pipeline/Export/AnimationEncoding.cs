using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>The animation containers Lux writes itself (no external tool): GIF, APNG and lossless animated WebP.</summary>
public enum AnimationContainer
{
    /// <summary>GIF89a: a palette of at most 256 colors, LZW, loop count, per-frame delays in hundredths of a second.</summary>
    Gif,
    /// <summary>Animated PNG: true color, deflate, exact pixels, per-frame delays in milliseconds.</summary>
    Apng,
    /// <summary>Animated WebP, lossless (VP8L): exact pixels, usually smaller than APNG.</summary>
    WebP,
}

/// <summary>How the GIF writer reduces each frame to 256 colors.</summary>
public enum GifPalette
{
    /// <summary>One palette for the whole animation (default): colors stay the same from frame to frame, so nothing flickers.</summary>
    Global,
    /// <summary>A palette per frame: slightly better color per frame, but colors can shift between frames.</summary>
    PerFrame,
}

/// <summary>Dithering of the GIF writer.</summary>
public enum GifDither
{
    /// <summary>None: flat color bands where the palette is coarse; the smallest files.</summary>
    None,
    /// <summary>An ordered (Bayer 8×8) pattern fixed to the pixel grid (default): still areas stay still from frame to frame, which also keeps
    /// the frame-to-frame differences small.</summary>
    Ordered,
    /// <summary>Error diffusion (Floyd–Steinberg): the smoothest gradients in a still, but the pattern moves between frames.</summary>
    Diffusion,
}

/// <summary>Options of the animation writers. Defaults are what Lux uses unless asked otherwise.</summary>
public sealed record AnimationEncodeOptions
{
    /// <summary>Times the animation plays; 0 = forever (default).</summary>
    public int Loops { get; init; }
    /// <summary>Write only the part of each frame that changed (all three containers). Never changes a decoded pixel.</summary>
    public bool FrameDifferencing { get; init; } = true;
    public GifPalette GifPalette { get; init; } = GifPalette.Global;
    public GifDither GifDither { get; init; } = GifDither.Ordered;
    /// <summary>Strength of the ordered dither in 8-bit levels, peak to peak (default 8: measured on real frames, 6 → 39.0 dB, 12 → 35.9 dB
    /// PSNR, with the bands of an undithered palette mostly gone).</summary>
    public double GifDitherStrength { get; init; } = 8;
    /// <summary>Colors in the palette, 2–256 (one is used for "unchanged" when <see cref="FrameDifferencing"/> is on).</summary>
    public int GifColours { get; init; } = 256;
    /// <summary>Deflate effort for APNG (default: optimal — measured within 1 % of "smallest size" at less than half the time).</summary>
    public System.IO.Compression.CompressionLevel PngCompression { get; init; } = System.IO.Compression.CompressionLevel.Optimal;
    /// <summary>WebP: search effort for repeated pixels (1–9, default 5). Never changes a pixel.</summary>
    public int WebPEffort { get; init; } = 5;
    /// <summary>Checked between frames.</summary>
    public CancellationToken Cancel { get; init; }
    /// <summary>Frames written so far / total (any thread).</summary>
    public Action<int, int>? Progress { get; init; }
}

/// <summary>What an encode produced.</summary>
/// <param name="Bytes">File size.</param>
/// <param name="DisplayedFrames">The frames a decoder shows (for GIF: after the palette reduction and dithering; for APNG / WebP: the input
/// frames themselves) — only kept when asked for (validation).</param>
public sealed record AnimationEncodeResult(long Bytes, int Frames, double Seconds, IReadOnlyList<Rgba>? DisplayedFrames)
{
    /// <summary>The delays actually written, ms (GIF stores hundredths of a second: rounded with the remainder carried, so the total holds).</summary>
    public IReadOnlyList<int> WrittenDelaysMs { get; init; } = Array.Empty<int>();
}

/// <summary>Writes an animation with Lux's own encoders.</summary>
public static class AnimationEncoder
{
    public static string Extension(AnimationContainer c) => c switch
    {
        AnimationContainer.Gif => ".gif", AnimationContainer.Apng => ".png", AnimationContainer.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static bool TryParse(string s, out AnimationContainer c)
    {
        switch (s.Trim().TrimStart('.').ToLowerInvariant())
        {
            case "gif": c = AnimationContainer.Gif; return true;
            case "apng" or "png": c = AnimationContainer.Apng; return true;
            case "webp": c = AnimationContainer.WebP; return true;
            default: c = default; return false;
        }
    }

    /// <summary>Encode <paramref name="frames"/> (all the same size) with per-frame delays into <paramref name="output"/>.</summary>
    public static AnimationEncodeResult Write(Stream output, AnimationContainer container, IReadOnlyList<Rgba> frames, IReadOnlyList<int> delaysMs,
                                              AnimationEncodeOptions? options = null, bool keepDisplayedFrames = false)
    {
        options ??= new AnimationEncodeOptions();
        if (frames.Count == 0) throw new ArgumentException("an animation needs at least one frame", nameof(frames));
        if (delaysMs.Count != frames.Count) throw new ArgumentException("one delay per frame", nameof(delaysMs));
        int w = frames[0].W, h = frames[0].H;
        foreach (var f in frames) if (f.W != w || f.H != h) throw new ArgumentException("every frame must have the same size", nameof(frames));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counting = new CountingStream(output);
        var r = container switch
        {
            AnimationContainer.Gif => GifWriter.Write(counting, frames, delaysMs, options, keepDisplayedFrames),
            AnimationContainer.Apng => ApngWriter.Write(counting, frames, delaysMs, options, keepDisplayedFrames),
            AnimationContainer.WebP => WebPWriter.Write(counting, frames, delaysMs, options, keepDisplayedFrames),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        counting.Flush();
        return r with { Bytes = counting.Written, Seconds = sw.Elapsed.TotalSeconds };
    }

    /// <summary>Encode into a file: written beside it under a temporary name and renamed into place when complete, so a canceled or failed
    /// encode leaves no partial file.</summary>
    public static AnimationEncodeResult WriteFile(string path, AnimationContainer container, IReadOnlyList<Rgba> frames, IReadOnlyList<int> delaysMs,
                                                  AnimationEncodeOptions? options = null, bool keepDisplayedFrames = false)
    {
        string tmp = path + ".part";
        try
        {
            AnimationEncodeResult r;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                r = Write(fs, container, frames, delaysMs, options, keepDisplayedFrames);
            File.Move(tmp, path, overwrite: true);
            return r;
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The smallest rectangle holding every pixel where <paramref name="a"/> and <paramref name="b"/> differ (RGB, or RGBA when
    /// <paramref name="alpha"/>); null when they are equal.</summary>
    internal static RectI? Changed(byte[] a, byte[] b, int w, int h, bool alpha = false)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        uint mask = alpha ? 0xFFFFFFFFu : 0x00FFFFFFu;
        var ua = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(a.AsSpan());
        var ub = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(b.AsSpan());
        for (int y = 0; y < h; y++)
        {
            int row = y * w, first = -1, last = -1;
            for (int x = 0; x < w; x++) if (((ua[row + x] ^ ub[row + x]) & mask) != 0) { first = x; break; }
            if (first < 0) continue;
            for (int x = w - 1; x >= first; x--) if (((ua[row + x] ^ ub[row + x]) & mask) != 0) { last = x; break; }
            if (first < x0) x0 = first;
            if (last > x1) x1 = last;
            if (y < y0) y0 = y;
            y1 = y;
        }
        return x1 < 0 ? null : new RectI(x0, y0, x1 + 1, y1 + 1);
    }

    /// <summary>GIF delays: hundredths of a second, rounded with the remainder carried (minimum 2 = 20 ms; browsers slow faster frames to
    /// 100 ms). Returns centiseconds.</summary>
    internal static int[] Centiseconds(IReadOnlyList<int> delaysMs)
    {
        var cs = new int[delaysMs.Count];
        double carry = 0;
        for (int i = 0; i < cs.Length; i++)
        {
            double want = delaysMs[i] / 10.0 + carry;
            int v = Math.Max(2, (int)Math.Round(want, MidpointRounding.AwayFromZero));
            carry = want - v;
            cs[i] = Math.Min(65535, v);
        }
        return cs;
    }

    sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Written += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); Written += buffer.Length; }
        public override void WriteByte(byte value) { inner.WriteByte(value); Written++; }
    }
}

/// <summary>CRC-32 (IEEE, reflected 0xEDB88320) — PNG chunk checksums.</summary>
internal static class Crc32
{
    static readonly uint[] Table = Build();
    static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    /// <summary>Continue a running CRC (start with 0xFFFFFFFF, finish with ^ 0xFFFFFFFF).</summary>
    public static uint Update(uint c, ReadOnlySpan<byte> data)
    {
        foreach (var x in data) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c;
    }
}
