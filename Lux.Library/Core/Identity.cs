using System.Security.Cryptography;
using Lux.Store;

namespace Lux.Library.Core;

/// <summary>What the scanner knows about a file without reading it: size and modification time of the file itself (a link's target, as
/// <see cref="ImageContext"/> sees it).</summary>
internal readonly record struct FileFacts(string Path, long Size, long MtimeTicks, bool Exists)
{
    public static FileFacts Stat(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget != null)
            {
                if (fi.ResolveLinkTarget(true) is not FileInfo t || !t.Exists) return new FileFacts(path, 0, 0, false);
                fi = t;
            }
            if (!fi.Exists) return new FileFacts(path, 0, 0, false);
            return new FileFacts(path, fi.Length, fi.LastWriteTimeUtc.Ticks, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new FileFacts(path, 0, 0, false); }
    }

    /// <summary>A cheap guard against a different file that happens to have the same size and modification time (copies with preserved
    /// times, a swap of two captures of the same size): SHA-256 over the size, the first 64 KiB and the last 64 KiB, first 16 bytes in hex.
    /// The first block of a capture holds its header (unique image id, time), so two captures never share it.</summary>
    public static string QuickFingerprint(string path, long size)
    {
        const int N = 64 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(size));
        var buf = new byte[N];
        int n = fs.ReadAtLeast(buf, (int)Math.Min(N, size), throwOnEndOfStream: false);
        sha.AppendData(buf, 0, n);
        if (size > N)
        {
            fs.Seek(Math.Max(N, size - N), SeekOrigin.Begin);
            n = fs.ReadAtLeast(buf, (int)Math.Min(N, size - Math.Max(N, size - N)), throwOnEndOfStream: false);
            sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }
}

/// <summary>
/// The content-hash cache (<see cref="IContentHashCache"/>) in the library database: path + size + modification time → SHA-256, so a
/// capture is hashed again only when one of them changes. Each hit is also checked against the file's quick fingerprint
/// (<see cref="FileFacts.QuickFingerprint"/>, 128 KiB read), so a different file with the same size and time is never given the old hash.
/// </summary>
internal sealed class HashCache : IContentHashCache
{
    readonly LibraryDb _db;
    public HashCache(LibraryDb db) { _db = db; }

    public bool TryGet(string fullPath, long size, DateTime modifiedUtc, out string hashHex)
    {
        hashHex = "";
        var row = _db.Read(c => c.Rows(null, "SELECT size, mtime, fp, sha256 FROM hash_cache WHERE path=$p",
                                        r => (Size: r.GetInt64(0), Mtime: r.GetInt64(1), Fp: r.GetString(2), Sha: r.GetString(3)), ("$p", fullPath)).FirstOrDefault());
        if (row.Sha is null || row.Size != size || row.Mtime != modifiedUtc.Ticks) return false;
        try { if (FileFacts.QuickFingerprint(fullPath, size) != row.Fp) return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        hashHex = row.Sha;
        return true;
    }

    public void Put(string fullPath, long size, DateTime modifiedUtc, string hashHex)
    {
        string fp;
        try { fp = FileFacts.QuickFingerprint(fullPath, size); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Put(fullPath, size, modifiedUtc.Ticks, fp, hashHex);
    }

    public void Put(string fullPath, long size, long mtimeTicks, string fp, string hashHex)
        => _db.Write((c, tx) => c.Exec(tx, """
            INSERT INTO hash_cache(path, size, mtime, fp, sha256, utc) VALUES($p, $s, $m, $f, $h, $u)
            ON CONFLICT(path) DO UPDATE SET size=excluded.size, mtime=excluded.mtime, fp=excluded.fp, sha256=excluded.sha256, utc=excluded.utc
            """, ("$p", fullPath), ("$s", size), ("$m", mtimeTicks), ("$f", fp), ("$h", hashHex), ("$u", DateTime.UtcNow.Ticks)));

    /// <summary>A known hash for this path whose size, time and fingerprint all still match; null otherwise.</summary>
    public string? Lookup(string fullPath, long size, long mtimeTicks, string fp)
        => _db.Read(c => c.Rows(null, "SELECT sha256 FROM hash_cache WHERE path=$p AND size=$s AND mtime=$m AND fp=$f", r => r.GetString(0),
                                ("$p", fullPath), ("$s", size), ("$m", mtimeTicks), ("$f", fp)).FirstOrDefault());
}
