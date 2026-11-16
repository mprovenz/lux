using Lux.Engine.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>Eight RGBA float pixels as four lane vectors (R, G, B, A) and back — pure data movement for the lane-parallel
/// per-pixel kernels. Callers check <see cref="Supported"/>.</summary>
internal static class Soa8
{
    public static bool Supported => Simd.IsWideSupported;

    /// <summary>Pixels p[0…7] (RGBA, 32 floats from <paramref name="p"/>) → R, G, B, A.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load(ref float p, out Vector256<float> r, out Vector256<float> g, out Vector256<float> b, out Vector256<float> a)
    {
        // four loads (two pixels each) and an in-register 4×8 transpose
        var v0 = Vector256.LoadUnsafe(ref p); var v1 = Vector256.LoadUnsafe(ref p, 8); var v2 = Vector256.LoadUnsafe(ref p, 16); var v3 = Vector256.LoadUnsafe(ref p, 24);
        // v0 = (p0 | p1), v1 = (p2 | p3), v2 = (p4 | p5), v3 = (p6 | p7) → regroup halves so each 128-bit lane pair holds (pk | pk+4)
        var w0 = Simd.Permute2x128(v0, v2, 0x20); var w1 = Simd.Permute2x128(v0, v2, 0x31);   // (p0 | p4), (p1 | p5)
        var w2 = Simd.Permute2x128(v1, v3, 0x20); var w3 = Simd.Permute2x128(v1, v3, 0x31);   // (p2 | p6), (p3 | p7)
        var t0 = Simd.UnpackLow(w0, w1); var t1 = Simd.UnpackHigh(w0, w1);                    // (r0 r1 g0 g1 | r4 r5 g4 g5), (b0 b1 a0 a1 | …)
        var t2 = Simd.UnpackLow(w2, w3); var t3 = Simd.UnpackHigh(w2, w3);                    // (r2 r3 g2 g3 | r6 r7 g6 g7), (b2 b3 a2 a3 | …)
        r = Simd.Shuffle(t0, t2, 0b01_00_01_00); g = Simd.Shuffle(t0, t2, 0b11_10_11_10);     // (r0 r1 r2 r3 | r4 r5 r6 r7)
        b = Simd.Shuffle(t1, t3, 0b01_00_01_00); a = Simd.Shuffle(t1, t3, 0b11_10_11_10);
    }

    /// <summary>R, G, B, A → pixels p[0…7] (32 floats from <paramref name="p"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(ref float p, Vector256<float> r, Vector256<float> g, Vector256<float> b, Vector256<float> a)
    {
        var t0 = Simd.UnpackLow(r, g); var t1 = Simd.UnpackHigh(r, g); var t2 = Simd.UnpackLow(b, a); var t3 = Simd.UnpackHigh(b, a);
        var u0 = Simd.Shuffle(t0, t2, 0b01_00_01_00); var u1 = Simd.Shuffle(t0, t2, 0b11_10_11_10);
        var u2 = Simd.Shuffle(t1, t3, 0b01_00_01_00); var u3 = Simd.Shuffle(t1, t3, 0b11_10_11_10);
        Simd.Permute2x128(u0, u1, 0x20).StoreUnsafe(ref p); Simd.Permute2x128(u2, u3, 0x20).StoreUnsafe(ref p, 8);
        Simd.Permute2x128(u0, u1, 0x31).StoreUnsafe(ref p, 16); Simd.Permute2x128(u2, u3, 0x31).StoreUnsafe(ref p, 24);
    }
}
