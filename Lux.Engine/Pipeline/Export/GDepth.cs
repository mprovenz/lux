using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// `ExportImageFormat` **4** — the companion JPEG plus Google's **GDepth** XMP depth map
/// (`a-display-isp.md` §12.4, `FUN_1805290f0` L400–600). Everything in this file is transcribed from cp.dll:
/// the float → gray8 encoder `FUN_18052e520`, the near/far scan, the two XMP templates and the extended-XMP
/// chunking. The literal strings are the `.rdata` blocks at `0x1806f3e2c` / `0x1806f3e4a` / `0x1806f3e6e` /
/// `0x1806f3f5d` / `0x1806f3f79` / `0x1806f40cc` / `0x1806f40db` / `0x1806f414b` / `0x1806f4162` / `0x1806f417d`,
/// read out of the binary rather than retyped.
///
/// The **depth source** is `DepthCache.cs`: `(*(exporter + 0xf8))(…)` → `FUN_1804cd2d0(renderer+0x480, …)` through the
/// lambda `0x180526ec0`, which adds `renderer+0x2d0[level]` to the rect and passes `&amp;renderer+0x288[level]`. The cache
/// holds `min(1/(1/FullDepth), 100000)` on the canvas grid at pipeline level 1 — see <see cref="DepthImageCache"/>.
///
/// **Verified byte-for-byte** against three cp.dll reference artefacts (the fmt-4 export run, which reaches the fmt-4 branch
/// `Lumen.exe` itself never takes): `o466_l3_gd.jpg` (1040×780), `o466_full_gd.jpg` (8320×6240) and `o306_l3_gd.jpg`
/// (1304×978) — every APP1 block, the MD5 guid, the chunking and the embedded grayscale depth JPEG match with **0**
/// differing bytes, `Near`/`Far` included.
/// </summary>
public static class GDepth
{
    // ---- the .rdata fragments, verbatim
    public const string StdNamespace = "http://ns.adobe.com/xap/1.0/";                 // 0x1806f3e2c, emitted with its NUL (0x1D bytes)
    public const string ExtNamespace = "http://ns.adobe.com/xmp/extension/";           // 0x1806f3e4a, with NUL = 0x23 bytes
    const string ExtPrefix = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Adobe XMP Core 5.1.0-jc003\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GDepth=\"http://ns.google.com/photos/1.0/depthmap/\" GDepth:Data=\"";   // 0x1806f3e6e, 238 B
    const string ExtSuffix = "\" /> </rdf:RDF></x:xmpmeta>";                            // 0x1806f3f5d, 27 B
    const string StdHead = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Adobe XMP Core 5.1.0-jc003\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GDepth=\"http://ns.google.com/photos/1.0/depthmap/\" xmlns:xmpNote=\"http://ns.adobe.com/xmp/note/\" GDepth:Format=\"RangeInverse\" GDepth:Mime=\"image/jpeg\" GDepth:Near=\"";   // 0x1806f3f79, 338 B
    const string StdFar = "\" GDepth:Far=\"";                                           // 0x1806f40cc
    const string StdMid = "\" GDepth:measureType=\"OpticalAxis\" GDepth:Manufacturer=\"Light Labs, Inc.\" GDepth:Units=\"mm\" GDepth:ImageWidth=\"";   // 0x1806f40db
    const string StdHeight = "\" GDepth:ImageHeight=\"";                                // 0x1806f414b
    const string StdGuid = "\" xmpNote:HasExtendedXMP=\"";                              // 0x1806f4162
    const string StdTail = "\" /></rdf:RDF></x:xmpmeta>";                               // 0x1806f417d

    /// <summary>`0xFFF5 − (35 + 32)` — the extended-XMP payload that fits one APP1 alongside the 67-byte header and
    /// the two big-endian `uint32`s.</summary>
    public const int MaxChunkPayload = 0xFFF5 - (35 + 32);

    /// <summary>The near/far scan of `0x180529d60–0x180529e59`: `min` seeded with `+FLT_MAX` (`DAT_1806b0ca4`) and
    /// `max` with `−FLT_MAX` (`DAT_1806bcd44`) over the **warped** float depth image.</summary>
    public static (float Near, float Far) NearFar(float[] depth, int w, int h, int stride)
    {
        float near = float.MaxValue, far = -float.MaxValue;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = depth[y * stride + x];
                if (v <= near) near = v;
                if (far <= v) far = v;
            }
        return (near, far);
    }

    /// <summary>
    /// `FUN_18052e520` with the expression `FUN_18052ecf0` builds: per sample
    /// <c>E = recip(d·(far − near)) · (d − near) · (255·far) + 0.5</c>, where `recip` is `rcpps` plus **one**
    /// Newton step `(1 − x·r)·r + r` in the vectorised body (512-pixel blocks) and a true `divss` in the row
    /// remainder (`0x18052e72e` / `0x18052eb69`). The row is then written by the float→u8 converter `0x1800944d0`,
    /// which applies its **own** `clamp(E + copysign(0.5, E), 0, 255)` + truncate — so the `+0.5` lands twice, and
    /// that is reproduced literally.
    /// </summary>
    public static byte[] EncodeGray8(float[] depth, int w, int h, int stride, float near, float far)
    {
        float span = far - near, k = 255.0f * far;
        var gray = new byte[(long)w * h];
        int blockEnd = (w / 512) * 512;   // local_fc: the rcpps body runs over whole 512-pixel blocks
        for (int y = 0; y < h; y++)
        {
            long o = (long)y * w; int so = y * stride;
            for (int x = 0; x < w; x++)
            {
                float d = depth[so + x];
                float t = d * span;
                float r;
                if (x < blockEnd)
                {
                    float a = System.Runtime.Intrinsics.Vector128.GetElement(
                        System.Runtime.Intrinsics.X86.Sse.Reciprocal(System.Runtime.Intrinsics.Vector128.Create(t)), 0);
                    r = (1.0f - t * a) * a + a;      // one Newton step, _DAT_1806824a0 = 1.0f
                }
                else r = 1.0f / t;                    // the scalar tail's divss
                float e = r * (d - near) * k + 0.5f;
                gray[o + x] = Isp.DisplayOutput.ExportByte(e);   // 1800944d0: +copysign(0.5) again, clamp, truncate
            }
        }
        return gray;
    }

    /// <summary>`precision(1)` + `std::fixed` (`FUN_18052edc0(stream, 2, 1)` on the ostringstream that formats
    /// Near/Far).</summary>
    public static string Fixed1(float v) => ((double)v).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Build the APP1 payloads: the standard XMP packet first, then the extension chunks in FIFO order —
    /// exactly the deque `0x1800fd9fe` drains into `jpeg_write_marker(0xE1, …)`.</summary>
    public static List<byte[]> BuildApp1(byte[] depthJpeg, float near, float far, int depthWidth, int depthHeight)
    {
        string extended = ExtPrefix + System.Convert.ToBase64String(depthJpeg) + ExtSuffix;   // FUN_18000ffe0, alphabet 0x180681ad0, '=' padded, unwrapped
        var extBytes = Encoding.ASCII.GetBytes(extended);
        // FUN_180010f20: plain MD5 (IV 0x180681c60) formatted with "%02X" → 32 UPPERCASE hex characters
        string guid = System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(extBytes));

        var std = new StringBuilder();
        std.Append(StdHead).Append(Fixed1(near)).Append(StdFar).Append(Fixed1(far)).Append(StdMid)
           .Append(depthWidth.ToString(CultureInfo.InvariantCulture)).Append(StdHeight)
           .Append(depthHeight.ToString(CultureInfo.InvariantCulture)).Append(StdGuid).Append(guid).Append(StdTail);

        var list = new List<byte[]>();
        using (var ms = new MemoryStream())
        {
            ms.Write(Encoding.ASCII.GetBytes(StdNamespace)); ms.WriteByte(0);
            ms.Write(Encoding.ASCII.GetBytes(std.ToString()));
            list.Add(ms.ToArray());
        }
        var header = new List<byte>();
        header.AddRange(Encoding.ASCII.GetBytes(ExtNamespace)); header.Add(0);
        header.AddRange(Encoding.ASCII.GetBytes(guid));
        for (int off = 0; off < extBytes.Length; off += MaxChunkPayload)
        {
            int n = Math.Min(MaxChunkPayload, extBytes.Length - off);
            var chunk = new byte[header.Count + 8 + n];
            header.CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(header.Count), (uint)extBytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(header.Count + 4), (uint)off);
            Array.Copy(extBytes, off, chunk, header.Count + 8, n);
            list.Add(chunk);
        }
        return list;
    }

    /// <summary>The whole fmt-4 depth side: resample the float depth to the output size with the **float**
    /// instantiations (the same two branches as the colour image, driven by the same `TransformOutput`), scan for
    /// near/far, encode gray8, wrap it in a `{98, 2}` **grayscale** JPEG with no COM and no Exif, and build the
    /// APP1 blocks.</summary>
    public static List<byte[]> Build(DepthImageCache cache, ExportLevels lv, TransformOutput to, (int W, int H) size)
    {
        var d = cache.FetchForExport(lv, to.Level, to.Source);
        return Build(d.ToDense(), d.W, d.H, to, size);
    }

    /// <summary>The depth image warped to the export grid, in millimetres and at full float precision — i.e. exactly
    /// what <see cref="Build(float[], int, int, TransformOutput, ValueTuple{int, int})"/> quantises to gray8. Split out
    /// so the depth map can also be written as data; the arithmetic is unchanged.</summary>
    public static float[] WarpToExport(float[] depthSrc, int sw, int sh, TransformOutput to, (int W, int H) size)
    {
        if (1.5f < to.ScaleX || 1.5f < to.ScaleY)
        {
            int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);
            var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
            var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
            var blurred = ConvSeparable1(depthSrc, sw, sh, kernelX, kernelY);
            return Warp1Bilinear(blurred, sw, sh, size.W, size.H, to);
        }
        return Warp1Clamped2(depthSrc, sw, sh, size.W, size.H, to);
    }

    /// <summary>The gray8 depth as a standalone grayscale JPEG — byte-for-byte the image embedded in `GDepth:Data`.</summary>
    public static byte[] DepthJpeg(float[] warped, (int W, int H) size, out float near, out float far)
    {
        (near, far) = NearFar(warped, size.W, size.H, size.W);
        var gray = EncodeGray8(warped, size.W, size.H, size.W, near, far);
        using var ms = new MemoryStream();
        JpegEncoder.Encode(ms, gray, size.W, size.H, size.W, grayscale: true, new JpegEncoder { Quality = 98, SubsamplingId = 2, Comment = null, ExifApp1 = null });
        return ms.ToArray();
    }

    public static List<byte[]> Build(float[] depthSrc, int sw, int sh, TransformOutput to, (int W, int H) size)
    {
        float[] warped = WarpToExport(depthSrc, sw, sh, to, size);
        var (near, far) = NearFar(warped, size.W, size.H, size.W);
        var gray = EncodeGray8(warped, size.W, size.H, size.W, near, far);
        using var ms = new MemoryStream();
        JpegEncoder.Encode(ms, gray, size.W, size.H, size.W, grayscale: true, new JpegEncoder { Quality = 98, SubsamplingId = 2, Comment = null, ExifApp1 = null });
        return BuildApp1(ms.ToArray(), near, far, size.W, size.H);
    }

    // ---- single-channel instantiations of the two resamplers (`ImageConvSeparable2D<float>`, `ImageWarpClamped<2,float>`
    //      `FUN_180530d80`, `ImageWarp<1,0,float>` `FUN_180530e60`); identical arithmetic to the vec4 versions.

    static float[] ConvSeparable1(float[] src, int w, int h, float[] kx, float[] ky)
    {
        int nx = kx.Length, ny = ky.Length, hx = (nx - 1) / 2, hy = (ny - 1) / 2;
        var rkx = new float[nx]; for (int i = 0; i < nx; i++) rkx[i] = kx[nx - 1 - i];
        var rky = new float[ny]; for (int i = 0; i < ny; i++) rky[i] = ky[ny - 1 - i];
        var tmp = new float[w * h]; var outp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int k = 0; k < ny; k++)
            {
                int sy = Math.Clamp(y - hy + k, 0, h - 1); float kv = rky[k]; int so = sy * w, tofs = y * w;
                if (k == 0) for (int i = 0; i < w; i++) tmp[tofs + i] = src[so + i] * kv;
                else for (int i = 0; i < w; i++) tmp[tofs + i] = src[so + i] * kv + tmp[tofs + i];
            }
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = 0; k < nx; k++) { int sx = Math.Clamp(x - hx + k, 0, w - 1); float v = tmp[y * w + sx] * rkx[k]; acc = k == 0 ? v : v + acc; }
                outp[y * w + x] = acc;
            }
        }
        return outp;
    }

    static readonly float[] Table = Lux.Engine.Pipeline.Geometry.WarpResample.BuildTable();

    static float[] Warp1Clamped2(float[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new float[(long)dw * dh]; var res = new float[4];
        Span<float> block = stackalloc float[64];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)((u + -1f) * 64f), py = (int)((v + -1f) * 64f);
                int ix = px >> 6, iy = py >> 6;
                if (!(ix < sw && 0 < ix + 4 && iy < sh && 0 < iy + 4)) continue;
                for (int r = 0; r < 4; r++)
                {
                    int cy = Math.Clamp(iy + r, 0, sh - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int cx = Math.Clamp(ix + c, 0, sw - 1);
                        block[(r * 4 + c) * 4] = src[(long)cy * sw + cx];
                        block[(r * 4 + c) * 4 + 1] = block[(r * 4 + c) * 4 + 2] = block[(r * 4 + c) * 4 + 3] = 0f;
                    }
                }
                Lux.Engine.Pipeline.Geometry.WarpResample.Resample(block, 4, 0, Table, px & 63, py & 63, res, 0);
                dst[(long)y * dw + x] = res[0];
            }
        return dst;
    }

    static float[] Warp1Bilinear(float[] src, int sw, int sh, int dw, int dh, TransformOutput to)
    {
        var dst = new float[(long)dw * dh];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                var (u, v) = to.Map((float)x, (float)y);
                int px = (int)(u * 64f), py = (int)(v * 64f);
                int ix = px >> 6, iy = py >> 6;
                int x0, x1, y0, y1;
                if (ix < 0 || sw - 2 < ix || iy < 0 || sh - 2 < iy)
                {
                    if (!(ix < sw && 0 < ix + 2 && iy < sh && 0 < iy + 2)) continue;
                    x0 = Math.Clamp(ix, 0, sw - 1); x1 = Math.Clamp(ix + 1, 0, sw - 1);
                    y0 = Math.Clamp(iy, 0, sh - 1); y1 = Math.Clamp(iy + 1, 0, sh - 1);
                }
                else { x0 = ix; x1 = ix + 1; y0 = iy; y1 = iy + 1; }
                float ty = (float)(py & 63) * 0.015625f, wy0 = 1f - ty, wy1 = ty;
                float tx = (float)(px & 63) * 0.015625f, wx0 = 1f - tx, wx1 = tx;
                dst[(long)y * dw + x] = (wy1 * src[(long)y1 * sw + x1] + wy0 * src[(long)y0 * sw + x1]) * wx1
                                      + (wy1 * src[(long)y1 * sw + x0] + wy0 * src[(long)y0 * sw + x0]) * wx0;
            }
        return dst;
    }
}
