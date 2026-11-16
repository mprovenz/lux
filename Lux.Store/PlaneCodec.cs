using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lux.Store;

/// <summary>
/// Lossless packing of image planes for the store's binary columns, with what .NET ships and nothing else.
/// A blob is one flag byte followed by the payload:
/// <code>
///   bits 0–1  compressor: 0 = stored, 1 = zlib (deflate), 2 = brotli
///   bit 4     the 32-bit values were split into four byte planes (all byte 0s, then all byte 1s, …) before compression
///   bit 5     every value was XOR-ed with the same element of a reference plane the reader also has
///   bit 6     every 32-bit value had its left neighbor subtracted (wrapping) — a horizontal predictor
/// </code>
/// The reader needs nothing but the flag byte and the element count. Float planes are handled as their bit patterns, so every value —
/// NaN payloads and negative zero included — comes back exactly.
/// </summary>
public static class PlaneCodec
{
    public const byte Stored = 0, ZLib = 1, Brotli = 2, CompressorMask = 3, Shuffle = 0x10, RefXor = 0x20, LeftDelta = 0x40;

    /// <summary>The compressor new blobs are written with (chosen by measurement).</summary>
    public static byte DefaultCompressor { get; set; } = Brotli;
    /// <summary>Brotli quality when <see cref="DefaultCompressor"/> is <see cref="Brotli"/>.</summary>
    public static int BrotliQuality { get; set; } = 5;
    /// <summary>zlib level for new blobs.</summary>
    public static CompressionLevel ZLibLevel { get; set; } = CompressionLevel.Optimal;

    public static byte[] Sha(ReadOnlySpan<float> v) => SHA256.HashData(MemoryMarshal.AsBytes(v));
    public static byte[] Sha(ReadOnlySpan<byte> v) => SHA256.HashData(v);

    static byte[] Compress(byte compressor, ReadOnlySpan<byte> src)
    {
        using var ms = new MemoryStream(src.Length / 2 + 64);
        if (compressor == ZLib) { using var z = new ZLibStream(ms, ZLibLevel, leaveOpen: true); z.Write(src); }
        else if (compressor == Brotli) { using var z = new BrotliStream(ms, new BrotliCompressionOptions { Quality = BrotliQuality }, leaveOpen: true); z.Write(src); }
        else throw new ArgumentException("unknown compressor");
        return ms.ToArray();
    }

    static void Decompress(byte compressor, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (compressor == Stored) { if (src.Length != dst.Length) throw new InvalidDataException("stored plane has the wrong length"); src.CopyTo(dst); return; }
        using var ms = new MemoryStream(src.ToArray(), writable: false);
        using Stream z = compressor == ZLib ? new ZLibStream(ms, CompressionMode.Decompress) : compressor == Brotli ? new BrotliStream(ms, CompressionMode.Decompress)
            : throw new InvalidDataException("unknown compressor in a stored plane");
        z.ReadExactly(dst);
        if (z.ReadByte() != -1) throw new InvalidDataException("stored plane is longer than expected");
    }

    static byte[] Finish(byte flags, byte compressor, byte[] prepared, int rawLength)
    {
        var c = Compress(compressor, prepared);
        if (c.Length >= rawLength && (flags & (RefXor | LeftDelta | Shuffle)) == 0) { var raw = new byte[rawLength + 1]; raw[0] = Stored; prepared.CopyTo(raw, 1); return raw; }
        var o = new byte[c.Length + 1]; o[0] = (byte)(flags | compressor); c.CopyTo(o, 1);
        return o;
    }

    /// <summary>One candidate encoding of a 32-bit plane.</summary>
    public static byte[] Encode32(ReadOnlySpan<uint> v, ReadOnlySpan<uint> reference, byte transform, byte compressor)
    {
        int n = v.Length; var w = new uint[n];
        if ((transform & RefXor) != 0)
        {
            if (reference.Length != n) throw new ArgumentException("the reference plane has a different size");
            for (int i = 0; i < n; i++) w[i] = v[i] ^ reference[i];
        }
        else v.CopyTo(w);
        if ((transform & LeftDelta) != 0) for (int i = n - 1; i > 0; i--) w[i] = unchecked(w[i] - w[i - 1]);
        var bytes = new byte[n * 4];
        if ((transform & Shuffle) != 0)
        {
            var src = MemoryMarshal.AsBytes(w.AsSpan());
            for (int p = 0; p < 4; p++) { int o = p * n; for (int i = 0; i < n; i++) bytes[o + i] = src[i * 4 + p]; }
        }
        else MemoryMarshal.AsBytes(w.AsSpan()).CopyTo(bytes);
        return Finish(transform, compressor, bytes, n * 4);
    }

    /// <summary>Pack a float plane: byte-plane split + the default compressor, and — when a reference plane is given — the same on the
    /// XOR with the reference; the smaller of the two is kept.</summary>
    public static byte[] EncodeFloats(ReadOnlySpan<float> v, ReadOnlySpan<float> reference = default)
    {
        var u = MemoryMarshal.Cast<float, uint>(v);
        if (reference.IsEmpty) return Encode32(u, default, Shuffle, DefaultCompressor);
        var xored = Encode32(u, MemoryMarshal.Cast<float, uint>(reference), (byte)(Shuffle | RefXor), DefaultCompressor);
        if ((long)xored.Length * 50 < (long)v.Length * 4) return xored;   // nearly identical to the reference: nothing to gain from a second try
        var plain = Encode32(u, default, Shuffle, DefaultCompressor);
        return xored.Length < plain.Length ? xored : plain;
    }

    public static float[] DecodeFloats(ReadOnlySpan<byte> blob, int count, ReadOnlySpan<float> reference = default)
    {
        if (blob.Length < 1) throw new InvalidDataException("empty plane blob");
        byte flags = blob[0]; var bytes = new byte[count * 4];
        Decompress((byte)(flags & CompressorMask), blob[1..], bytes);
        var o = new float[count]; var u = MemoryMarshal.Cast<float, uint>(o.AsSpan());
        if ((flags & Shuffle) != 0)
        {
            var dst = MemoryMarshal.AsBytes(o.AsSpan());
            for (int p = 0; p < 4; p++) { int off = p * count; for (int i = 0; i < count; i++) dst[i * 4 + p] = bytes[off + i]; }
        }
        else bytes.CopyTo(MemoryMarshal.AsBytes(o.AsSpan()));
        if ((flags & LeftDelta) != 0) for (int i = 1; i < count; i++) u[i] = unchecked(u[i] + u[i - 1]);
        if ((flags & RefXor) != 0)
        {
            if (reference.Length != count) throw new InvalidDataException("a stored plane needs its reference plane");
            var r = MemoryMarshal.Cast<float, uint>(reference);
            for (int i = 0; i < count; i++) u[i] ^= r[i];
        }
        return o;
    }

    /// <summary>Pack a byte plane (a layer mask), optionally as the XOR with a reference plane.</summary>
    public static byte[] EncodeBytes(ReadOnlySpan<byte> v, ReadOnlySpan<byte> reference = default)
    {
        var best = Finish(0, DefaultCompressor, v.ToArray(), v.Length);
        if (!reference.IsEmpty)
        {
            if (reference.Length != v.Length) throw new ArgumentException("the reference plane has a different size");
            var x = new byte[v.Length]; for (int i = 0; i < x.Length; i++) x[i] = (byte)(v[i] ^ reference[i]);
            var alt = Finish(RefXor, DefaultCompressor, x, v.Length);
            if (alt.Length < best.Length) best = alt;
        }
        return best;
    }

    public static byte[] DecodeBytes(ReadOnlySpan<byte> blob, int count, ReadOnlySpan<byte> reference = default)
    {
        if (blob.Length < 1) throw new InvalidDataException("empty plane blob");
        byte flags = blob[0]; var o = new byte[count];
        Decompress((byte)(flags & CompressorMask), blob[1..], o);
        if ((flags & RefXor) != 0)
        {
            if (reference.Length != count) throw new InvalidDataException("a stored plane needs its reference plane");
            for (int i = 0; i < count; i++) o[i] ^= reference[i];
        }
        return o;
    }

    /// <summary>Human-readable form of a blob's flag byte.</summary>
    public static string Describe(byte flags)
    {
        string c = (flags & CompressorMask) switch { Stored => "stored", ZLib => "zlib", Brotli => "brotli", _ => "?" };
        return c + ((flags & Shuffle) != 0 ? "+byteplanes" : "") + ((flags & RefXor) != 0 ? "+refxor" : "") + ((flags & LeftDelta) != 0 ? "+leftdelta" : "");
    }
}
