using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Export;

namespace Lux.Cli;

/// <summary>
/// `convert --formats depth` — the capture's own stereo depth map, on the ported registration chain.
///
/// The depth is the one the pipeline itself uses: <see cref="ExportBuild.Build(string,int,Action{string})"/> at level 0 runs
/// `StereoAsyncApi` (features → matching → bundle adjust → dense stereo → `DenseUpsampleLayer`) and leaves the
/// upsampled full-frame result in <see cref="ExportState.FullDepth"/>. That is the same image
/// `RendererPrivate::setInputDataStream` puts in the renderer's depth `ImageCache`, so this, the `jpg+depth`
/// (`ExportImageFormat` 4) export and the parallax formats read one source, resampled onto the export grid by
/// exactly the `GetExportTransformOutput` transform the image writers resolve — including its rotation, so a depth
/// map written alongside a rotated raster format lands on the same grid as that image.
///
/// Values are **metric millimetres** (`InverseDepth` → `DepthImageCache`, far clipped at 100 000 mm), so a sample
/// is a real distance and two captures are directly comparable.
/// </summary>
public static class DepthCmd
{
    public sealed record Result(string F32Path, string PreviewPath, int Width, int Height, float NearMm, float FarMm);

    /// <summary>Resample a full-frame depth onto the export grid (<see cref="ExportSession.DepthOnGrid"/>).</summary>
    public static float[] Warp(float[] depth, int dw, int dh, ExportLevels win, ExportTransform tr, (int W, int H) size)
    {
        var cache = DepthImageCache.FromFullDepth(depth, dw, dh, win);
        // The same TransformOutput the export writers resolve (`GetExportTransformOutput`), so the depth lands on
        // exactly the exported image's grid.
        var dto = ExportTransformOutput.Compute(tr, size, new RectI(0, 0, size.W, size.H), win.ExportDims, false);
        var fetched = cache.FetchForExport(win, dto.Level, dto.Source);
        return GDepth.WarpToExport(fetched.ToDense(), fetched.W, fetched.H, dto, size);
    }

    /// <summary>Write a depth already on the export grid as `&lt;stem&gt;_depth.f32` (the `{w,h,stride,bpp=4}` header +
    /// float32 millimetres) plus `&lt;stem&gt;_depth.jpg` (the gray8 preview, byte for byte the image the `GDepth:Data`
    /// block carries).</summary>
    public static Result Write(float[] warped, (int W, int H) size, string outDir, string stem)
    {
        Directory.CreateDirectory(outDir);
        string f32 = Path.Combine(outDir, $"{stem}_depth.f32");
        using (var fs = File.Create(f32))
        {
            Span<byte> hdr = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr, size.W);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], size.H);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], size.W);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], 4);
            fs.Write(hdr);
            fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(warped));
        }
        var jpg = GDepth.DepthJpeg(warped, size, out float near, out float far);
        string preview = Path.Combine(outDir, $"{stem}_depth.jpg");
        File.WriteAllBytes(preview, jpg);
        return new Result(f32, preview, size.W, size.H, near, far);
    }
}
