using System.Buffers.Binary;
using System.IO.Compression;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>An 8-bit RGBA image, row-major, tightly packed (stride = 4·W). The currency of the parallax formats: the
/// export renderer hands out exactly this layout (<see cref="Export.JpegExportRenderer.Render"/>), so no conversion
/// happens between the verified pipeline's output and the warper's input.</summary>
public sealed class Rgba
{
    public readonly int W, H;
    public readonly byte[] P;

    public Rgba(int w, int h) { W = w; H = h; P = new byte[(long)w * h * 4]; }
    public Rgba(int w, int h, byte[] p) { W = w; H = h; P = p; }

    public Rgba Clone() => new(W, H, (byte[])P.Clone());

    /// <summary>Rec.601 luma as float, one plane. Used by every matcher and blur.</summary>
    public float[] Luma()
    {
        var g = new float[(long)W * H];
        for (long i = 0, p = 0; i < g.LongLength; i++, p += 4)
            g[i] = 0.299f * P[p] + 0.587f * P[p + 1] + 0.114f * P[p + 2];
        return g;
    }

    /// <summary>Bilinear resample to an exact size (the working-size downscale of the export image).</summary>
    public Rgba Resize(int ow, int oh)
    {
        if (ow == W && oh == H) return this;
        var o = new Rgba(ow, oh);
        double sx = (double)W / ow, sy = (double)H / oh;
        Parallel.For(0, oh, y =>
        {
            double fy = (y + 0.5) * sy - 0.5; int y0 = (int)Math.Floor(fy); double ty = fy - y0;
            int ya = Math.Clamp(y0, 0, H - 1), yb = Math.Clamp(y0 + 1, 0, H - 1);
            for (int x = 0; x < ow; x++)
            {
                double fx = (x + 0.5) * sx - 0.5; int x0 = (int)Math.Floor(fx); double tx = fx - x0;
                int xa = Math.Clamp(x0, 0, W - 1), xb = Math.Clamp(x0 + 1, 0, W - 1);
                int pa = (ya * W + xa) * 4, pb = (ya * W + xb) * 4, pc = (yb * W + xa) * 4, pd = (yb * W + xb) * 4;
                int d = (y * ow + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double v = (P[pa + c] * (1 - tx) + P[pb + c] * tx) * (1 - ty) + (P[pc + c] * (1 - tx) + P[pd + c] * tx) * ty;
                    o.P[d + c] = (byte)Math.Clamp(v + 0.5, 0, 255);
                }
            }
        });
        return o;
    }
}

/// <summary>A single-channel float plane — the metric depth (millimetres) pixel-aligned to an <see cref="Rgba"/>.</summary>
public sealed class Plane
{
    public readonly int W, H;
    public readonly float[] V;
    public Plane(int w, int h) { W = w; H = h; V = new float[(long)w * h]; }
    public Plane(int w, int h, float[] v) { W = w; H = h; V = v; }
    public float this[int x, int y] { get => V[(long)y * W + x]; set => V[(long)y * W + x] = value; }

    /// <summary>Nearest-neighbour resample. Depth is piecewise-constant across object boundaries; interpolating it
    /// there invents surfaces that are not in the scene and shows up as a smeared halo after the warp.</summary>
    public Plane ResizeNearest(int ow, int oh)
    {
        if (ow == W && oh == H) return this;
        var o = new Plane(ow, oh);
        double sx = (double)W / ow, sy = (double)H / oh;
        Parallel.For(0, oh, y =>
        {
            int sy0 = Math.Clamp((int)((y + 0.5) * sy), 0, H - 1);
            for (int x = 0; x < ow; x++) o.V[(long)y * ow + x] = V[(long)sy0 * W + Math.Clamp((int)((x + 0.5) * sx), 0, W - 1)];
        });
        return o;
    }

    public float Percentile(double p)
    {
        var s = (float[])V.Clone(); Array.Sort(s);
        return s[Math.Clamp((int)(p * (s.Length - 1)), 0, s.Length - 1)];
    }
}

/// <summary>PNG output for the still parallax formats. A 60-line encoder (Sub-filtered scanlines through
/// <see cref="ZLibStream"/>) keeps the stills lossless without adding a dependency.</summary>
public static class Png
{
    public static void Write(string path, Rgba img, bool alpha = false)
    {
        using var fs = File.Create(path);
        Span<byte> sig = stackalloc byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        fs.Write(sig);
        int bpp = alpha ? 4 : 3;
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.W); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.H);
        ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);

        var raw = new byte[(long)(img.W * bpp + 1) * img.H];
        long o = 0;
        for (int y = 0; y < img.H; y++)
        {
            raw[o++] = 1;   // Sub filter: cheap and much better than none on photographic rows
            long p = (long)y * img.W * 4;
            for (int x = 0; x < img.W; x++)
                for (int c = 0; c < bpp; c++)
                {
                    byte cur = img.P[p + x * 4 + c];
                    byte left = x > 0 ? img.P[p + (x - 1) * 4 + c] : (byte)0;
                    raw[o++] = (byte)(cur - left);
                }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint c = Crc(t, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, c); s.Write(len);
    }

    static readonly uint[] Table = BuildTable();
    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    static uint Crc(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Binary PPM (P6) output — the frame format handed to the animation encoder.</summary>
public static class Ppm
{
    public static void Write(string path, Rgba img)
    {
        using var fs = File.Create(path);
        fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{img.W} {img.H}\n255\n"));
        var row = new byte[img.W * 3];
        for (int y = 0; y < img.H; y++)
        {
            long p = (long)y * img.W * 4;
            for (int x = 0; x < img.W; x++) { row[x * 3] = img.P[p + x * 4]; row[x * 3 + 1] = img.P[p + x * 4 + 1]; row[x * 3 + 2] = img.P[p + x * 4 + 2]; }
            fs.Write(row);
        }
    }
}
