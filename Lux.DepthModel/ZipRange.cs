using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lux.DepthModel;

/// <summary>A zip file that does not have the expected structure, or that does not hold the expected entry.</summary>
public sealed class ZipStructureException : Exception
{
    public ZipStructureException(string message) : base(message) { }
}

/// <summary>One entry of a zip file, as the central directory describes it.</summary>
/// <param name="Method">0 = stored, 8 = Deflate.</param>
/// <param name="DataOffset">The position of the entry's data in the zip file.</param>
public sealed record ZipRangeEntry(string Name, int Method, uint Crc32, long CompressedSize, long Size, long DataOffset);

/// <summary>
/// Reads one entry of a zip file without the rest of the file. The reader asks for byte ranges: the end record (with the Zip64 records
/// when the file has them), the central directory and the header in front of the entry. The caller then reads the entry's data from
/// <see cref="ZipRangeEntry.DataOffset"/> and unpacks it with <see cref="Unpack"/>.
/// </summary>
public static class ZipRange
{
    /// <summary>Returns <c>length</c> bytes of the zip file from <c>offset</c>.</summary>
    public delegate Task<byte[]> Reader(long offset, int length);

    const int EndSize = 22, MaxComment = 65535, FirstTail = 16384, MaxDirectory = 64 << 20;
    const uint EndSig = 0x06054b50, Zip64LocatorSig = 0x07064b50, Zip64EndSig = 0x06064b50, DirectorySig = 0x02014b50, LocalSig = 0x04034b50;

    static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    static long I64(ReadOnlySpan<byte> b, int at)
    {
        long v = BinaryPrimitives.ReadInt64LittleEndian(b[at..]);
        return v >= 0 ? v : throw new ZipStructureException("The zip file has a size that is out of range.");
    }

    /// <summary>Find the entry <paramref name="name"/> in a zip file of <paramref name="fileSize"/> bytes.</summary>
    /// <exception cref="ZipStructureException">The file is not a zip file, the entry is missing, or the entry cannot be read (encrypted,
    /// or a method other than stored and Deflate).</exception>
    public static async Task<ZipRangeEntry> FindAsync(Reader read, long fileSize, string name)
    {
        if (fileSize < EndSize) throw new ZipStructureException("The file is too small for a zip file.");

        // the end record: in the last bytes of the file, in front of an optional comment
        int tailLength = (int)Math.Min(fileSize, FirstTail);
        byte[] tail = await read(fileSize - tailLength, tailLength).ConfigureAwait(false);
        int end = FindEnd(tail);
        if (end < 0 && tailLength < Math.Min(fileSize, EndSize + MaxComment))
        {
            tailLength = (int)Math.Min(fileSize, EndSize + MaxComment);
            tail = await read(fileSize - tailLength, tailLength).ConfigureAwait(false);
            end = FindEnd(tail);
        }
        if (end < 0) throw new ZipStructureException("The file has no zip end record.");
        long tailOffset = fileSize - tailLength, endOffset = tailOffset + end;

        // a part of the zip file that is in the tail already, or one more read
        async Task<byte[]> Part(long offset, long length)
        {
            if (offset < 0 || length < 0 || length > MaxDirectory || offset + length > endOffset) throw new ZipStructureException("The zip directory is out of range.");
            if (offset >= tailOffset) return tail.AsSpan((int)(offset - tailOffset), (int)length).ToArray();
            return await read(offset, (int)length).ConfigureAwait(false);
        }

        long count = U16(tail, end + 10), directorySize = U32(tail, end + 12), directoryOffset = U32(tail, end + 16);
        if (U16(tail, end + 4) != 0 && U16(tail, end + 4) != 0xFFFF) throw new ZipStructureException("The zip file is one part of several.");
        if (count == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
        {
            // Zip64: a locator in front of the end record gives the position of the larger end record
            byte[] locator = await Part(endOffset - 20, 20).ConfigureAwait(false);
            if (U32(locator, 0) != Zip64LocatorSig) throw new ZipStructureException("The zip file has no Zip64 locator.");
            byte[] end64 = await Part(I64(locator, 8), 56).ConfigureAwait(false);
            if (U32(end64, 0) != Zip64EndSig) throw new ZipStructureException("The zip file has no Zip64 end record.");
            count = I64(end64, 32); directorySize = I64(end64, 40); directoryOffset = I64(end64, 48);
        }

        byte[] directory = await Part(directoryOffset, directorySize).ConfigureAwait(false);
        var (method, crc, compressed, size, header) = FindInDirectory(directory, count, name);

        // the header in front of the data: the same name, then an extra field of its own length
        int nameLength = Encoding.UTF8.GetByteCount(name);
        if (header + 30 + nameLength > directoryOffset) throw new ZipStructureException($"The zip entry {name} is out of range.");
        byte[] local = await read(header, 30 + nameLength).ConfigureAwait(false);
        if (U32(local, 0) != LocalSig || U16(local, 26) != nameLength || Encoding.UTF8.GetString(local, 30, nameLength) != name || U16(local, 8) != method)
            throw new ZipStructureException($"The zip entry {name} does not agree with the zip directory.");
        long data = header + 30 + nameLength + U16(local, 28);
        if (data + compressed > directoryOffset) throw new ZipStructureException($"The zip entry {name} is out of range.");
        return new ZipRangeEntry(name, method, crc, compressed, size, data);
    }

    /// <summary>The record of the entry <paramref name="name"/> in the central directory.</summary>
    static (int Method, uint Crc, long Compressed, long Size, long Header) FindInDirectory(ReadOnlySpan<byte> directory, long count, string name)
    {
        int at = 0;
        for (long i = 0; i < count; i++)
        {
            if (at + 46 > directory.Length || U32(directory, at) != DirectorySig) throw new ZipStructureException("The zip directory is damaged.");
            int flags = U16(directory, at + 8), method = U16(directory, at + 10);
            uint crc = U32(directory, at + 16);
            long compressed = U32(directory, at + 20), size = U32(directory, at + 24), header = U32(directory, at + 42);
            int nameLength = U16(directory, at + 28), extraLength = U16(directory, at + 30), commentLength = U16(directory, at + 32);
            int next = at + 46 + nameLength + extraLength + commentLength;
            if (next > directory.Length) throw new ZipStructureException("The zip directory is damaged.");
            if (Encoding.UTF8.GetString(directory.Slice(at + 46, nameLength)) != name) { at = next; continue; }

            // Zip64: the values that do not fit are in an extra field, in this order
            var extra = directory.Slice(at + 46 + nameLength, extraLength);
            for (int e = 0; e + 4 <= extra.Length; e += 4 + U16(extra, e + 2))
            {
                if (U16(extra, e) != 1) continue;
                var values = extra[(e + 4)..Math.Min(extra.Length, e + 4 + U16(extra, e + 2))];
                int p = 0;
                if (size == 0xFFFFFFFF) size = Value(values, ref p);
                if (compressed == 0xFFFFFFFF) compressed = Value(values, ref p);
                if (header == 0xFFFFFFFF) header = Value(values, ref p);
                break;
            }
            if ((flags & 1) != 0) throw new ZipStructureException($"The zip entry {name} is encrypted.");
            if (method is not (0 or 8)) throw new ZipStructureException($"The zip entry {name} uses a compression method that Lux does not read.");
            if (method == 0 && compressed != size) throw new ZipStructureException($"The zip entry {name} has sizes that do not agree.");
            return (method, crc, compressed, size, header);
        }
        throw new ZipStructureException($"The zip file does not contain {name}.");
    }

    static long Value(ReadOnlySpan<byte> values, ref int at)
    {
        if (at + 8 > values.Length) throw new ZipStructureException("The zip directory is damaged.");
        long v = I64(values, at); at += 8;
        return v;
    }

    /// <summary>The position of the end record in the last bytes of a zip file; -1 when there is none. The record's comment ends where the
    /// file ends.</summary>
    static int FindEnd(ReadOnlySpan<byte> tail)
    {
        for (int at = tail.Length - EndSize; at >= 0; at--)
            if (U32(tail, at) == EndSig && at + EndSize + U16(tail, at + 20) == tail.Length) return at;
        return -1;
    }

    /// <summary>Unpack the data of <paramref name="entry"/> from <paramref name="data"/> to <paramref name="to"/>.</summary>
    /// <returns>False when the data is damaged, or when its size or CRC-32 differs from the zip directory.</returns>
    public static bool Unpack(ZipRangeEntry entry, Stream data, Stream to)
    {
        using Stream source = entry.Method == 8 ? new DeflateStream(data, CompressionMode.Decompress, leaveOpen: true) : data;
        var buffer = new byte[1 << 16];
        uint crc = 0xFFFFFFFF; long size = 0;
        try
        {
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                size += n;
                if (size > entry.Size) return false;
                foreach (byte b in buffer.AsSpan(0, n)) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                to.Write(buffer, 0, n);
            }
        }
        catch (InvalidDataException) { return false; }
        return size == entry.Size && ~crc == entry.Crc32;
    }

    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
