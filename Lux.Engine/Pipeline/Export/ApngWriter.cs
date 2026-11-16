using System.Buffers.Binary;
using System.IO.Compression;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Lux's animated PNG writer (APNG, no external tool): true color (8-bit RGB), every pixel exact. The first frame is the PNG's own image
/// (a viewer without APNG support shows it as a still); each later frame stores only the rectangle that changed since the previous one
/// (<c>fcTL</c> region, blend "source", disposal "none"). Rows are filtered adaptively (the filter with the smallest sum of absolute
/// differences per row) and deflated with .NET's zlib. Delays are written as milliseconds (<c>delay_num / 1000</c>).
/// </summary>
public static class ApngWriter
{
    internal static AnimationEncodeResult Write(Stream s, IReadOnlyList<Rgba> frames, IReadOnlyList<int> delaysMs, AnimationEncodeOptions o, bool keep)
    {
        int w = frames[0].W, h = frames[0].H;
        var bw = new BufferedStream(s, 1 << 16);
        bw.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;   // 8-bit RGB
        Chunk(bw, "IHDR", ihdr);
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames.Count); BinaryPrimitives.WriteInt32BigEndian(actl.AsSpan(4), Math.Max(0, o.Loops));
        Chunk(bw, "acTL", actl);
        // each frame's rectangle and compressed rows are independent of the others: made in parallel, written in order
        var rects = new RectI[frames.Count];
        var data = new byte[frames.Count][];
        int done = 0;
        Parallel.For(0, frames.Count, new ParallelOptions { CancellationToken = o.Cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
        {
            rects[f] = o.FrameDifferencing && f > 0 ? AnimationEncoder.Changed(frames[f - 1].P, frames[f].P, w, h) ?? new RectI(0, 0, 1, 1) : new RectI(0, 0, w, h);
            data[f] = Compress(frames[f], rects[f], o.PngCompression);
            o.Progress?.Invoke(Interlocked.Increment(ref done), frames.Count);
        });
        uint seq = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            var rect = rects[f];
            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, seq++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), rect.Width); BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), rect.Height);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(12), rect.X0); BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(16), rect.Y0);
            int ms = Math.Max(0, delaysMs[f]);
            (ushort num, ushort den) = ms <= 65535 ? ((ushort)ms, (ushort)1000) : ((ushort)Math.Min(65535, (ms + 5) / 10), (ushort)100);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), num); BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), den);
            fctl[24] = 0;   // dispose: none
            fctl[25] = 0;   // blend: source
            Chunk(bw, "fcTL", fctl);
            if (f == 0) Chunk(bw, "IDAT", data[f]);
            else
            {
                var fd = new byte[data[f].Length + 4];
                BinaryPrimitives.WriteUInt32BigEndian(fd, seq++);
                Buffer.BlockCopy(data[f], 0, fd, 4, data[f].Length);
                Chunk(bw, "fdAT", fd);
            }
        }
        Chunk(bw, "IEND", Array.Empty<byte>());
        bw.Flush();
        return new AnimationEncodeResult(0, frames.Count, 0, keep ? frames : null) { WrittenDelaysMs = delaysMs.ToArray() };
    }

    /// <summary>The rectangle's rows, each with its adaptive filter byte, deflated (zlib).</summary>
    static byte[] Compress(Rgba img, RectI r, CompressionLevel level)
    {
        int rw = r.Width, rh = r.Height, stride = rw * 3;
        var raw = new byte[(long)(stride + 1) * rh];
        Parallel.For(0, rh, y =>
        {
            var cur = new byte[stride]; var prev = new byte[stride];
            Row(img, r, y, cur);
            if (y > 0) Row(img, r, y - 1, prev);
            var cand = new byte[stride];
            long bestSum = long.MaxValue; int bestF = 0;
            var bestRow = new byte[stride];
            for (int f = 0; f < 5; f++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 3 ? cur[i - 3] : 0, b = y > 0 ? prev[i] : 0, c = i >= 3 && y > 0 ? prev[i - 3] : 0;
                    int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                    byte v = (byte)(cur[i] - pred);
                    cand[i] = v; sum += (sbyte)v < 0 ? -(sbyte)v : v;
                    if (sum >= bestSum) break;
                }
                if (sum < bestSum) { bestSum = sum; bestF = f; Buffer.BlockCopy(cand, 0, bestRow, 0, stride); }
            }
            long o2 = (long)y * (stride + 1);
            raw[o2] = (byte)bestF;
            Buffer.BlockCopy(bestRow, 0, raw, (int)(o2 + 1), stride);
        });
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    static void Row(Rgba img, RectI r, int y, byte[] dst)
    {
        long p = ((long)(r.Y0 + y) * img.W + r.X0) * 4;
        for (int x = 0, o = 0; x < r.Width; x++, p += 4, o += 3) { dst[o] = img.P[p]; dst[o + 1] = img.P[p + 1]; dst[o + 2] = img.P[p + 2]; }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        Span<byte> t = stackalloc byte[4];
        for (int i = 0; i < 4; i++) t[i] = (byte)type[i];
        s.Write(t); s.Write(data);
        uint c = Crc32.Update(Crc32.Update(0xFFFFFFFFu, t), data);
        BinaryPrimitives.WriteUInt32BigEndian(len, c ^ 0xFFFFFFFFu); s.Write(len);
    }
}
