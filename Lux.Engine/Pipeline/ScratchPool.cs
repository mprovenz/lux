using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Lux.Engine.Pipeline;

/// <summary>
/// Reused scratch arrays for the per-tile temporaries of the depth effect and the display output stage. Without the pool, each tile
/// allocates every intermediate image afresh — about 100 MB per depth-effect tile, all on the large-object heap. With many tiles in
/// flight, the collector then runs a full (gen-2) collection every ~60 ms, and the speed-up stops at about four threads. A rented
/// array is NOT cleared (use
/// <see cref="RentZeroed{T}"/> when the kernel relies on zeros) and may be longer than asked for: callers index it by their own width / height,
/// never by its length, and never hand it out. Pooling changes no pixel: every kernel writes each element it later reads.
/// <para>Arrays are kept by size class (the length rounded up to an eighth of its power of two, at most 12.5 % slack), at most
/// <see cref="MaxPooledBytes"/> in all; a size class nobody has used for <see cref="IdleMs"/> is released, so an idle app holds nothing.</para>
/// </summary>
public static class ScratchPool
{
    /// <summary>Arrays shorter than this (elements) are simply allocated (small-object heap, cheap).</summary>
    public const int MinPooled = 16 * 1024;
    /// <summary>Upper bound of the bytes held for reuse.</summary>
    public static long MaxPooledBytes = 512L << 20;
    /// <summary>A size class unused for this long is emptied.</summary>
    public static int IdleMs = 2000;

    sealed class Bag
    {
        public readonly Stack<Array> Items = new();
        public readonly long Bytes;
        public long LastUse;
        public Bag(long bytes) { Bytes = bytes; }
    }
    static readonly ConcurrentDictionary<(Type, int), Bag> Bags = new();
    static long _pooledBytes;
    static Timer? _trim;
    static readonly object TrimGate = new();

    /// <summary>The bytes currently held for reuse.</summary>
    public static long PooledBytes => Interlocked.Read(ref _pooledBytes);

    /// <summary>The length a request of <paramref name="n"/> elements is served with.</summary>
    public static int SizeClass(int n)
    {
        if (n < MinPooled) return n;
        int p = 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)n));
        int step = p >> 3;
        long len = ((long)n + step - 1) / step * step;
        return len > int.MaxValue ? n : (int)len;
    }

    static Bag BagOf<T>(int len) where T : unmanaged => Bags.GetOrAdd((typeof(T), len), static k => new Bag((long)k.Item2 * Unsafe.SizeOf<T>()));

    /// <summary>An array of at least <paramref name="n"/> elements with undefined contents.</summary>
    public static T[] Rent<T>(int n) where T : unmanaged
    {
        if (n < MinPooled) return new T[n];
        int len = SizeClass(n);
        var bag = BagOf<T>(len);
        lock (bag)
        {
            bag.LastUse = Environment.TickCount64;
            if (bag.Items.Count > 0) { Interlocked.Add(ref _pooledBytes, -bag.Bytes); return (T[])bag.Items.Pop(); }
        }
        return GC.AllocateUninitializedArray<T>(len);
    }

    /// <summary>An array of at least <paramref name="n"/> elements whose first <paramref name="n"/> are zero.</summary>
    public static T[] RentZeroed<T>(int n) where T : unmanaged
    {
        var a = Rent<T>(n);
        Array.Clear(a, 0, n);
        return a;
    }

    /// <summary>Give an array back (null and short arrays are ignored). The caller must not use it afterwards.</summary>
    public static void Return<T>(T[]? a) where T : unmanaged
    {
        if (a is null || a.Length < MinPooled || SizeClass(a.Length) != a.Length) return;
        var bag = BagOf<T>(a.Length);
        if (Interlocked.Add(ref _pooledBytes, bag.Bytes) > MaxPooledBytes) { Interlocked.Add(ref _pooledBytes, -bag.Bytes); return; }
        lock (bag) { bag.Items.Push(a); bag.LastUse = Environment.TickCount64; }
        if (_trim is null) lock (TrimGate) _trim ??= new Timer(_ => Trim(false), null, 1000, 1000);
    }

    /// <summary>Release the size classes unused for <see cref="IdleMs"/> (all of them with <paramref name="all"/>).</summary>
    public static void Trim(bool all = true)
    {
        long now = Environment.TickCount64;
        foreach (var bag in Bags.Values)
            lock (bag)
                if (all || now - bag.LastUse > IdleMs)
                    while (bag.Items.Count > 0) { bag.Items.Pop(); Interlocked.Add(ref _pooledBytes, -bag.Bytes); }
    }
}
