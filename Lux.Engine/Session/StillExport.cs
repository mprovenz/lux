using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Isp.Stages;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Session;

/// <summary>
/// The still formats of Lux (<see cref="StillFormat"/>): the 16-bit TIFF, the scene-linear TIFF and the HDR gain-map JPEG. Each one
/// runs the output stage of the JPG on the same tiles and takes the floating-point result, where the JPG stores 8 bits:
/// <list type="bullet">
///   <item>`tiff16` takes the result as it is (sRGB), or with the tone curve applied and the color space left at linear ProPhoto RGB,
///   which this class then converts to Display P3 or Adobe RGB. The values are stored at 16 bits.</item>
///   <item>`tiff-linear` runs the output stage without the tone curve and stores the values, with the exposure gain applied, as
///   floating-point numbers. A value of 1 is the input that the tone curve maps to white. Larger values are kept.</item>
///   <item>`jpg-hdr` makes the HDR rendition (<see cref="HdrRendition"/>) from the image without the tone curve, and stores its ratio to
///   the JPG's own pixels as a gain map (<see cref="GainMapJpeg"/>).</item>
/// </list>
/// The geometry is that of the JPG: the same level, the same source rectangle and the same resampling rule, in floating point.
/// </summary>
public static class StillExport
{
    /// <summary>Takes one region of the output image: <paramref name="rgba"/> holds its pixels row by row, 4 values for each pixel.
    /// Regions do not overlap, and several threads call the sink at the same time.</summary>
    public delegate void RegionSink(RectI region, float[] rgba);

    /// <summary>The output stage of one level with the output color space set to linear ProPhoto RGB.</summary>
    /// <param name="Gain">The exposure gain of the tone stage of the JPG.</param>
    /// <param name="ToneType">The tone curve of the JPG (`tone_mapping.type`).</param>
    sealed record LinearStage(SoftIsp Isp, IspStats Stats, float Gain, string ToneType);

    /// <summary>The output stage of the JPG for <paramref name="level"/>, changed to give linear ProPhoto RGB. With
    /// <paramref name="keepTone"/> the tone curve stays. Without it the tone stage only passes the values on, and the caller applies
    /// <see cref="LinearStage.Gain"/>. All other settings, and thus all adjustments, are those of the JPG.</summary>
    static LinearStage LinearStageOf(ExportSession s, int level, bool keepTone)
    {
        var (jpgIsp, jpgStats) = s.IspOfLevel(level);
        var tuning = jpgIsp.Tuning.Clone();
        string tone = tuning.Type("tone_mapping");
        float ev = tuning.Has("tone_mapping.ev_offset") ? (float)tuning.Num("tone_mapping.ev_offset") : 0f;
        tuning.Set("output.color_space", "linear_prophoto_rgb");
        if (!keepTone) tuning.Set("tone_mapping.type", "linear").Set("tone_mapping.ev_offset", 0.0);
        var stats = jpgStats.WithOutSpace(ColorSpace.ProPhotoD50);
        return new LinearStage(new SoftIsp(tuning, s.State.Colour).UseStats(stats), stats, MuslMath.Exp2f(ev), tone);
    }

    /// <summary>
    /// The merged image of a sensor at its limit, before the output stage. The merged image holds camera values with the lens vignetting
    /// corrected: each value is the sensor value times the gain of the vignetting model at that position. A sensor at its limit gives
    /// 1, so its merged image is <see cref="Color"/> times those gains.
    /// </summary>
    /// <param name="Color">The camera color: that of a white surface, with its largest channel at 1.</param>
    /// <param name="Gains">The grid of the vignetting gains of the reference lens (<see cref="LensShadingKernel.ModelGrid"/>).</param>
    sealed record SensorLimit(float[] Color, int Cols, int Rows, float[] Gains);

    /// <summary>
    /// Renders the output image of the session's grid through <paramref name="isp"/> as floating-point values and gives it to
    /// <paramref name="sink"/> in regions.
    /// </summary>
    /// <param name="limit">Null, or the image of a sensor at its limit (<see cref="SensorLimit"/>). Then the fourth value of each pixel
    /// is not alpha: it is the largest channel that <paramref name="isp"/> gives at that position for that image. The caller uses it
    /// to learn the level of the sensor limit at each pixel.</param>
    static void RenderFloat(ExportSession s, SoftIsp isp, IspStats stats, SensorLimit? limit, string phase, RegionSink sink)
    {
        var st = s.State; var req = s.Request; var win = s.Grid.Window; var size = s.Grid.Size;
        var to = ExportTransformOutput.Compute(s.Grid.Transform, size, new RectI(0, 0, size.W, size.H), win.ExportDims, false);
        int level = to.Level; var src = to.Source; int sw = src.Width, sh = src.Height;
        var (lw, lh) = win.ExportDims[level];
        if (src.X0 < 0 || src.Y0 < 0 || src.X1 > lw || src.Y1 > lh) throw new ArgumentException("export source rect out of bounds");
        // The common case: the output is the source rectangle, pixel for pixel. Then each tile goes to the sink directly.
        bool direct = src.X0 == 0 && src.Y0 == 0 && sw == size.W && sh == size.H
                      && to.Map(0f, 0f) == (0f, 0f) && to.Map(1f, 0f) == (1f, 0f) && to.Map(0f, 1f) == (0f, 1f);
        int nx = ExportLevels.GridCount(lw), ny = ExportLevels.GridCount(lh);
        int tx0 = Math.Min(src.X0 / ExportLevels.Tile, nx - 1), tx1 = Math.Min((src.X1 - 1) / ExportLevels.Tile, nx - 1);
        int ty0 = Math.Min(src.Y0 / ExportLevels.Tile, ny - 1), ty1 = Math.Min((src.Y1 - 1) / ExportLevels.Tile, ny - 1);
        var tiles = new List<(int Tx, int Ty)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) tiles.Add((tx, ty));
        var fetch = s.DisplayTileSource();
        var pd = win.PipelineDims[level];
        float[]? gathered = direct ? null : new float[checked(sw * sh * 4)];
        int threads = Math.Max(1, req.RenderThreads);
        s.Progress.Begin(phase, tiles.Count);

        void One((int Tx, int Ty) t)
        {
            var tile = win.TileRect(level, t.Tx, t.Ty);
            var geo = DisplayRender.Geometry(tile, pd, win.Origins[level], win.CacheDims, level);
            var px = fetch(win.BaseLevel + geo.CacheLevel, geo.Grown, pd);
            var grown = new Image<Vec4F>(geo.Grown.Width, geo.Grown.Height);
            MemoryMarshal.Cast<float, Vec4F>(px.AsSpan()).CopyTo(grown.Data);
            var img = DisplayRender.Run(isp, st.Frame, stats, grown, geo, level);
            Image<Vec4F>? limitImage = null;
            if (limit is not null)
            {
                int gw = geo.Grown.Width, gh = geo.Grown.Height;
                var flat = new Image<Vec4F>(gw, gh);
                flat.Data.AsSpan().Fill(new Vec4F(limit.Color[0], limit.Color[1], limit.Color[2], 1f));
                LensShadingKernel.Apply(flat, new RectI(0, 0, gw, gh), geo.Float, gw, gh, win.CacheDims.W, win.CacheDims.H, limit.Cols, limit.Rows, limit.Gains);
                limitImage = DisplayRender.Run(isp, st.Frame, stats, flat, geo, level);
            }
            var c = tile.Intersect(src);
            if (c.IsEmpty) throw new InvalidOperationException("a tile of the render does not overlap the source rectangle");
            var dst = gathered ?? new float[c.Width * c.Height * 4];
            for (int y = c.Y0; y < c.Y1; y++)
            {
                var row = img.Row(y - tile.Y0);
                var lrow = limitImage is null ? default : limitImage.Row(y - tile.Y0);
                int o = gathered is null ? (y - c.Y0) * c.Width * 4 : ((y - src.Y0) * sw + (c.X0 - src.X0)) * 4;
                for (int x = c.X0; x < c.X1; x++)
                {
                    var v = row[x - tile.X0];
                    float a = v.A;
                    if (limitImage is not null) { var l = lrow[x - tile.X0]; a = MathF.Max(l.R, MathF.Max(l.G, l.B)); }
                    dst[o++] = v.R; dst[o++] = v.G; dst[o++] = v.B; dst[o++] = a;
                }
            }
            if (gathered is null) sink(c, dst);
            s.Progress.Tick();
        }
        if (threads <= 1 || tiles.Count <= 1) { foreach (var t in tiles) { req.Cancel.ThrowIfCancellationRequested(); One(t); } }
        else Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = req.Cancel }, One);
        if (gathered is null) return;

        // The resampling rule of the JPG, in floating point: a Lanczos blur and a bilinear warp for a reduction of more than 1.5,
        // otherwise the Catmull-Rom warp.
        float[] outp;
        if (1.5f < to.ScaleX || 1.5f < to.ScaleY)
        {
            int kx = (int)(to.ScaleX * 3.5f), ky = (int)(to.ScaleY * 3.5f);
            var kernelX = ExportResample.LanczosKernel((kx & ~1) + 1, to.ScaleX);
            var kernelY = ExportResample.LanczosKernel((ky & ~1) + 1, to.ScaleY);
            outp = ExportResample.WarpBilinear(ExportResample.ConvSeparable(gathered, sw, sh, kernelX, kernelY), sw, sh, size.W, size.H, to);
        }
        else outp = ExportResample.WarpClamped2(gathered, sw, sh, size.W, size.H, to, threads);
        const int Band = 256;
        int bands = (size.H + Band - 1) / Band;
        void OneBand(int k)
        {
            int y0 = k * Band, y1 = Math.Min(size.H, y0 + Band);
            var band = new float[(y1 - y0) * size.W * 4];
            Array.Copy(outp, (long)y0 * size.W * 4, band, 0, band.Length);
            sink(new RectI(0, y0, size.W, y1), band);
        }
        if (threads <= 1 || bands <= 1) for (int k = 0; k < bands; k++) OneBand(k);
        else Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = req.Cancel }, OneBand);
    }

    static int LevelOf(ExportSession s)
        => ExportTransformOutput.Compute(s.Grid.Transform, s.Grid.Size, new RectI(0, 0, s.Grid.Size.W, s.Grid.Size.H), s.Grid.Window.ExportDims, false).Level;

    // ---------------------------------------------------------------------------------------------------- TIFF

    /// <summary>Writes the `tiff16` file: the picture of the JPG at 16 bits per channel.</summary>
    public static void WriteTiff16(ExportSession s, string path, DngExportTags tags, TiffOptions options)
    {
        var (W, H) = s.Grid.Size;
        int level = LevelOf(s);
        var samples = new ushort[checked(W * H * 3)];
        var cs = options.ColorSpace;
        if (cs == TiffColorSpace.Srgb)
        {
            // the floating-point pixels of the JPG itself
            var (isp, stats) = s.IspOfLevel(level);
            RenderFloat(s, isp, stats, null, "tiff16 tiles", (region, px) =>
            {
                int i = 0;
                for (int y = region.Y0; y < region.Y1; y++)
                {
                    int o = (y * W + region.X0) * 3;
                    for (int x = region.X0; x < region.X1; x++, i += 4) { samples[o++] = Code16(px[i]); samples[o++] = Code16(px[i + 1]); samples[o++] = Code16(px[i + 2]); }
                }
            });
        }
        else
        {
            // the tone curve of the JPG in linear ProPhoto RGB, then the matrix and the transfer function of the color space
            var stage = LinearStageOf(s, level, keepTone: true);
            var target = ColorSpace.Standard(cs == TiffColorSpace.DisplayP3 ? ColorSpace.LinearDisplayP3 : 6, WhitePoint.Of(7), 1);
            var T = ColorSpaceConvert.BuildTLinear(ColorSpace.ProPhotoD50, target, ColorSpace.Adaptation(ColorSpace.ProPhotoD50.Wp, target.Wp, 1));
            bool p3 = cs == TiffColorSpace.DisplayP3;
            const float AdobeExponent = 256f / 563f;   // Adobe RGB (1998): gamma 563/256
            float Encode(float v)
            {
                if (!(v > 0f)) return 0f;
                if (v >= 1f) return 1f;
                return p3 ? ColorSpaceConvert.SrgbEncode(v) : WineCrt.Powf(v, AdobeExponent);
            }
            RenderFloat(s, stage.Isp, stage.Stats, null, "tiff16 tiles", (region, px) =>
            {
                int i = 0;
                for (int y = region.Y0; y < region.Y1; y++)
                {
                    int o = (y * W + region.X0) * 3;
                    for (int x = region.X0; x < region.X1; x++, i += 4)
                    {
                        float r = px[i], g = px[i + 1], b = px[i + 2];
                        samples[o++] = Code16(Encode(T[0] * r + T[1] * g + T[2] * b));
                        samples[o++] = Code16(Encode(T[3] * r + T[4] * g + T[5] * b));
                        samples[o++] = Code16(Encode(T[6] * r + T[7] * g + T[8] * b));
                    }
                }
            });
        }
        var image = new TiffImage
        {
            Width = W, Height = H, BitsPerSample = 16, FloatingPoint = false, Compression = options.Compression, Threads = Math.Max(1, s.Request.RenderThreads),
            IccProfile = IccProfiles.ForTiff(cs), Tags = TiffTags(tags, W, H, srgb: cs == TiffColorSpace.Srgb),
        };
        WriteFile(path, fs => TiffImageWriter.Write(fs, image, (y, row) => MemoryMarshal.AsBytes(samples.AsSpan(y * W * 3, W * 3)).CopyTo(row), s.Progress, "tiff16 write", s.Request.Cancel));
    }

    /// <summary>Writes a file. When the writer fails or is canceled, the incomplete file is removed.</summary>
    static void WriteFile(string path, Action<Stream> write)
    {
        try
        {
            using var fs = File.Create(path);
            write(fs);
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>The 16-bit code of a value from 0 to 1: rounded to the nearest code and limited to the range. Not-a-number gives 0.</summary>
    static ushort Code16(float v)
    {
        float t = v * 65535f + 0.5f;
        return !(t > 0f) ? (ushort)0 : t >= 65535f ? (ushort)65535 : (ushort)t;
    }

    /// <summary>Writes the `tiff-linear` file: the image before the tone curve, in linear ProPhoto RGB, as floating-point values.</summary>
    public static void WriteTiffLinear(ExportSession s, string path, DngExportTags tags, TiffOptions options)
    {
        var (W, H) = s.Grid.Size;
        var stage = LinearStageOf(s, LevelOf(s), keepTone: false);
        float gain = stage.Gain;
        bool half = options.LinearDepth == TiffFloatDepth.Float16;
        // one array holds the samples in their file form: the bits of each float, or of each half
        var bits32 = half ? null : new float[checked(W * H * 3)];
        var bits16 = half ? new ushort[checked(W * H * 3)] : null;
        RenderFloat(s, stage.Isp, stage.Stats, null, "tiff-linear tiles", (region, px) =>
        {
            int i = 0;
            for (int y = region.Y0; y < region.Y1; y++)
            {
                int o = (y * W + region.X0) * 3;
                for (int x = region.X0; x < region.X1; x++, i += 4)
                    for (int c = 0; c < 3; c++, o++)
                    {
                        float v = px[i + c] * gain;
                        if (bits32 is not null) bits32[o] = v; else bits16![o] = BitConverter.HalfToUInt16Bits((Half)v);
                    }
            }
        });
        var image = new TiffImage
        {
            Width = W, Height = H, BitsPerSample = half ? 16 : 32, FloatingPoint = true, Compression = options.Compression, Threads = Math.Max(1, s.Request.RenderThreads),
            IccProfile = IccProfiles.LinearProPhoto, Tags = TiffTags(tags, W, H, srgb: false),
        };
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("the TIFF writer needs a little-endian machine");
        WriteFile(path, fs => TiffImageWriter.Write(fs, image, (y, row) =>
        {
            if (bits32 is not null) MemoryMarshal.AsBytes(bits32.AsSpan(y * W * 3, W * 3)).CopyTo(row);
            else MemoryMarshal.AsBytes(bits16!.AsSpan(y * W * 3, W * 3)).CopyTo(row);
        }, s.Progress, "tiff-linear write", s.Request.Cancel));
    }

    /// <summary>
    /// The descriptive tags of a TIFF file: Make, Model, Software and DateTime in the main directory, and an Exif directory with the
    /// values that the JPG has (Exif 3.0, with the capture dates and their time zone in the Exif directory).
    /// </summary>
    static TiffDirectory TiffTags(DngExportTags t, int width, int height, bool srgb)
    {
        var ifd0 = new TiffDirectory(); var exif = new TiffDirectory();
        ifd0.SetSub(0x8769, exif);
        if (t.CameraIsLight) { ifd0.Ascii(0x10f, "Light"); ifd0.Ascii(0x110, "L16"); }
        ifd0.Ascii(0x131, t.Software);
        var m = t.ModifyTime;
        ifd0.Ascii(0x132, $"{m.Year:D4}:{m.Month:D2}:{m.Day:D2} {m.Hour:D2}:{m.Minute:D2}:{m.Second:D2}");
        exif.Undefined(0x9000, Encoding.ASCII.GetBytes("0300"));                           // ExifVersion 3.0
        exif.Rational(0x829d, TiffDirectory.Rat24(t.FNumber));                            // FNumber
        exif.Long(0x8833, (uint)t.Iso); exif.Short(0x8827, t.Iso); exif.Short(0x8830, 1);  // ISOSpeed, the ISO value, SensitivityType
        exif.Rational(0x920a, TiffDirectory.Rat24((float)t.FocalLengthMm));               // FocalLength
        exif.Short(0x9209, 0);                                                             // Flash: not fired
        exif.Ascii(0xa420, Convert.ToHexString(t.UniqueId).ToLowerInvariant());            // ImageUniqueID
        if (t.TimeStamp is { } ts)
        {   // DateTimeOriginal, DateTimeDigitized and their time zone
            string d = $"{ts.Year:D4}:{ts.Month:D2}:{ts.Day:D2} {ts.Hour:D2}:{ts.Minute:D2}:{ts.Second:D2}";
            exif.Ascii(0x9003, d); exif.Ascii(0x9004, d);
            string o = Offset(ts.TzOffsetMinutes);
            exif.Ascii(0x9011, o); exif.Ascii(0x9012, o);
        }
        exif.Ascii(0x9010, Offset(t.ModifyTzOffsetHours * 60));                            // OffsetTime, of DateTime
        float et = t.ExposureTimeSeconds;
        if (et > 0f && (double)et < 0.01) exif.Rational(0x829a, (1u, (uint)(int)(1.0 / (double)et + 0.5)));
        else exif.Rational(0x829a, TiffDirectory.Rat24(et));                              // ExposureTime
        exif.SRational(0x9204, TiffDirectory.SRat24(t.ExposureCompensation));              // ExposureBiasValue
        exif.Short(0xa001, srgb ? 1 : 0xffff);                                             // ColorSpace: sRGB or uncalibrated
        exif.Long(0xa002, (uint)width); exif.Long(0xa003, (uint)height);                   // PixelXDimension, PixelYDimension
        return ifd0;

        static string Offset(int minutes) { int a = Math.Abs(minutes); return $"{(minutes < 0 ? "-" : "+")}{a / 60:D2}:{a % 60:D2}"; }
    }

    // ---------------------------------------------------------------------------------------------------- HDR gain-map JPEG

    /// <summary>What <see cref="BuildJpegHdr"/> made.</summary>
    /// <param name="File">The bytes of the file.</param>
    /// <param name="PrimaryLength">The length of the JPG that the file starts from.</param>
    /// <param name="PeakLuminance">The largest luminance of the HDR rendition, as a multiple of SDR white.</param>
    /// <param name="AtLimitShare">The part of the pixels that the rendition changed toward neutral, because they are near the sensor limit.</param>
    public sealed record JpegHdrResult(byte[] File, int PrimaryLength, GainMapMetadata Metadata, int MapWidth, int MapHeight, int MapBytes, float PeakLuminance, double AtLimitShare);

    /// <summary>The offset that the gain formula adds to the base value and to the HDR value (the value of the Ultra HDR Image Format).</summary>
    const float GainOffset = 1f / 64f;

    /// <summary>
    /// Builds the `jpg-hdr` file for the session's grid.
    /// </summary>
    /// <param name="primary">The encoder of the JPG, without an ICC profile. The primary image of the file is the JPG that this encoder
    /// writes from the session's 8-bit render.</param>
    /// <param name="renditionSink">Null, or a sink that takes the HDR rendition: linear values with the sRGB primaries, SDR white at 1,
    /// in the first three values of each pixel. The fourth value is the level of the sensor limit at the pixel.</param>
    public static JpegHdrResult BuildJpegHdr(ExportSession s, GainMapOptions options, JpegEncoder primary, Action<string>? log = null, RegionSink? renditionSink = null)
    {
        options.Validate();
        if (primary.IccProfile is not null) throw new ArgumentException("the encoder of the primary image must not embed an ICC profile");
        if (s.Request.ColorSpaceOf(ExportImageFormat.Jpeg) != 4) throw new ExportRequestException("jpg-hdr needs the sRGB color space for the JPG");
        var (W, H) = s.Grid.Size;
        var (_, rgba) = s.Rgba8();
        byte[] primaryJpeg;
        using (var ms = new MemoryStream())
        {
            JpegEncoder.Encode(ms, rgba, W, H, W * 4, grayscale: false, primary);
            primaryJpeg = ms.ToArray();
        }

        var stage = LinearStageOf(s, LevelOf(s), keepTone: false);
        int mode = StageEnums.Enum("tone_mapping", stage.ToneType) - 3;
        if (mode < 0) throw new ExportRequestException($"jpg-hdr needs one of Lumen's tone curves; the tone mapping of this render is '{stage.ToneType}'");
        var rendition = new HdrRendition(TmoAcrLuts.ForMode(mode), stage.Gain);

        // A white surface at the sensor limit: the camera color of white, with its largest channel at 1, in an image with the
        // vignetting of the reference lens corrected.
        var neutral = stage.Stats.Neutral;
        float nmax = MathF.Max(neutral[0], MathF.Max(neutral[1], neutral[2]));
        SensorLimit? limitFill = null;
        if (nmax > 0f && neutral.All(v => v > 0f))
        {
            var grid = LensShadingKernel.ModelGrid(s.State.Frame.Header, s.State.Frame.Module);
            limitFill = new SensorLimit(new[] { neutral[0] / nmax, neutral[1] / nmax, neutral[2] / nmax }, grid.Cols, grid.Rows, LensShadingKernel.Transform(grid.Data, 1f, inverse: false));
        }

        var srgb = ColorSpace.Standard(2, WhitePoint.Of(ColorSpace.NativeIlluminantOf(2)), 1);
        var M = ColorSpaceConvert.BuildTSrgb(ColorSpace.ProPhotoD50, srgb, ColorSpace.Adaptation(ColorSpace.ProPhotoD50.Wp, srgb.Wp, 1));
        var decode = new float[256];   // the sRGB transfer function of IEC 61966-2-1, code to linear
        for (int i = 0; i < 256; i++) { double v = i / 255.0; decode[i] = (float)(v <= 0.04045 ? v / 12.92 : WineCrt.Pow((v + 0.055) / 1.055, 2.4)); }

        int sc = options.Scale, ch = options.Rgb ? 3 : 1;
        int gw = (W + sc - 1) / sc, gh = (H + sc - 1) / sc;
        var sums = new float[checked(gw * gh * ch)];
        float peak = 0f, topChannel = 0f, limitLow = float.PositiveInfinity, limitHigh = 0f; long atLimit = 0; var sync = new object();
        RenderFloat(s, stage.Isp, stage.Stats, limitFill, "gain map tiles", (region, px) =>
        {
            if (region.X0 % sc != 0 || region.Y0 % sc != 0) throw new InvalidOperationException("a region of the render does not start on the gain map grid");
            // the lowest sensor limit of the region: a pixel below its start of the change to neutral needs no check of its own
            float minLimit = float.PositiveInfinity, regionLow = float.PositiveInfinity, regionHigh = 0f;
            if (limitFill is not null)
            {
                for (int i = 3; i < px.Length; i += 4) { if (px[i] < regionLow) regionLow = px[i]; if (px[i] > regionHigh) regionHigh = px[i]; }
                regionLow = rendition.LevelOf(regionLow); regionHigh = rendition.LevelOf(regionHigh);
                minLimit = regionLow * HdrRendition.NeutralStart;
            }
            var hdr = renditionSink is null ? null : new float[px.Length];
            float regionPeak = 0f, regionTop = 0f; long regionAtLimit = 0;
            int k = 0;
            for (int y = region.Y0; y < region.Y1; y++)
            {
                long o = ((long)y * W + region.X0) * 4;
                int grow = (y / sc) * gw;
                for (int x = region.X0; x < region.X1; x++, k += 4, o += 4)
                {
                    float sr = decode[rgba[o]], sg = decode[rgba[o + 1]], sb = decode[rgba[o + 2]];
                    float r = px[k], g = px[k + 1], b = px[k + 2];
                    if (hdr is not null) hdr[k + 3] = limitFill is null ? 0f : rendition.LevelOf(px[k + 3]);
                    if (rendition.IsBelowKnee(r, g, b))
                    {   // the rendition is the picture of the JPG here: gain 1, which adds 0 to the sums
                        if (hdr is not null) { hdr[k] = sr; hdr[k + 1] = sg; hdr[k + 2] = sb; }
                        continue;
                    }
                    rendition.Pixel(r, g, b, out float R, out float G, out float B);
                    // (a luminance is never above the largest channel, so this test passes each pixel that can change)
                    if (MathF.Max(R, MathF.Max(G, B)) > minLimit && HdrRendition.ToNeutralAtLimit(ref R, ref G, ref B, rendition.LevelOf(px[k + 3]))) regionAtLimit++;
                    float hr = MathF.Max(0f, M[0] * R + M[1] * G + M[2] * B), hg = MathF.Max(0f, M[3] * R + M[4] * G + M[5] * B), hb = MathF.Max(0f, M[6] * R + M[7] * G + M[8] * B);
                    if (hdr is not null) { hdr[k] = hr; hdr[k + 1] = hg; hdr[k + 2] = hb; }
                    float yh = 0.2126f * hr + 0.7152f * hg + 0.0722f * hb;
                    if (yh > regionPeak) regionPeak = yh;
                    float top = MathF.Max(hr, MathF.Max(hg, hb));
                    if (top > regionTop) regionTop = top;
                    int gi = (grow + x / sc) * ch;
                    if (ch == 3)
                    {
                        sums[gi] += WineCrt.Log2f((hr + GainOffset) / (sr + GainOffset));
                        sums[gi + 1] += WineCrt.Log2f((hg + GainOffset) / (sg + GainOffset));
                        sums[gi + 2] += WineCrt.Log2f((hb + GainOffset) / (sb + GainOffset));
                    }
                    else
                    {
                        float ys = 0.2126f * sr + 0.7152f * sg + 0.0722f * sb;
                        sums[gi] += WineCrt.Log2f((yh + GainOffset) / (ys + GainOffset));
                    }
                }
            }
            lock (sync)
            {
                if (regionPeak > peak) peak = regionPeak;
                if (regionTop > topChannel) topChannel = regionTop;
                if (regionLow < limitLow) limitLow = regionLow;
                if (regionHigh > limitHigh) limitHigh = regionHigh;
                atLimit += regionAtLimit;
            }
            renditionSink?.Invoke(region, hdr!);
        });

        // the mean log2 gain of each block
        for (int gy = 0; gy < gh; gy++)
        {
            int rowsIn = Math.Min(sc, H - gy * sc);
            for (int gx = 0; gx < gw; gx++)
            {
                float inv = 1f / (rowsIn * Math.Min(sc, W - gx * sc));
                int gi = (gy * gw + gx) * ch;
                for (int c = 0; c < ch; c++) sums[gi + c] *= inv;
            }
        }
        // The range of the map. A few extreme gains would take precision from all others, so both ends leave out 0.05 % of the values.
        // Such gains come from a color channel that is near 0 in one rendition only. The minimum is 0 (no pixel darker than the JPG)
        // unless more than those few are darker, and not below −2. The maximum is not below the gain that takes a white pixel of the
        // JPG to the brightest value of the rendition, so that range is always complete.
        float max = 0f;
        var sample = new List<float>(sums.Length / 7 + 1);
        for (int i = 0; i < sums.Length; i++) { if (sums[i] > max) max = sums[i]; if (i % 7 == 0) sample.Add(sums[i]); }
        sample.Sort();
        float q = sample[(int)(sample.Count * 0.0005)], qTop = sample[Math.Min(sample.Count - 1, (int)(sample.Count * 0.9995))];
        // (a color outside sRGB can have a channel above the peak of the rendition; the peak is the largest value that counts)
        topChannel = MathF.Min(topChannel, HdrRendition.Peak);
        float whiteGain = topChannel > 1f ? WineCrt.Log2f((topChannel + GainOffset) / (1f + GainOffset)) : 0f;
        double lo = q > -0.02f ? 0.0 : Math.Max(q, -2.0);
        double hi = Math.Max(Math.Min(max, Math.Max(qTop, whiteGain)), lo + 0.01);
        // The headroom that shows the rendition completely: log2 of its brightest value over SDR white, at most log2 of the peak.
        double capacity = Math.Max(topChannel > 1f ? WineCrt.Log2f(topChannel) : 0f, 0.1);
        // rounded to the precision of the metadata, so that the XMP values and the ISO values are the same numbers
        lo = Math.Round(lo, 6); hi = Math.Round(hi, 6); capacity = Math.Round(capacity, 6);
        var metadata = new GainMapMetadata { GainMapMin = lo, GainMapMax = hi, HdrCapacityMin = 0.0, HdrCapacityMax = capacity };
        float range = (float)(hi - lo), flo = (float)lo;
        var map = new byte[checked(gw * gh * (ch == 1 ? 1 : 4))];
        for (int i = 0; i < gw * gh; i++)
            for (int c = 0; c < ch; c++)
            {
                float m = (sums[i * ch + c] - flo) / range;
                m = !(m > 0f) ? 0f : m > 1f ? 1f : m;
                map[ch == 1 ? i : i * 4 + c] = (byte)(m * 255f + 0.5f);
            }
        byte[] mapJpeg;
        using (var ms = new MemoryStream())
        {
            JpegEncoder.Encode(ms, map, gw, gh, ch == 1 ? gw : gw * 4, grayscale: ch == 1, new JpegEncoder { Quality = options.Quality, SubsamplingId = 0 });
            mapJpeg = ms.ToArray();
        }
        var file = GainMapJpeg.Assemble(primaryJpeg, mapJpeg, metadata, IccProfiles.Srgb);
        double share = (double)atLimit / ((double)W * H);
        log?.Invoke(FormattableString.Invariant($"gain map: {gw}x{gh} {(ch == 3 ? "RGB" : "luminance")}, {mapJpeg.Length} B, log2 gain {lo:0.###} to {hi:0.###}, HDR capacity {capacity:0.###}, peak luminance {peak:0.##}x SDR white, sensor limit at {limitLow:0.##}x to {limitHigh:0.##}x, {share * 100:0.##} % of the pixels near it"));
        return new JpegHdrResult(file, primaryJpeg.Length, metadata, gw, gh, mapJpeg.Length, peak, share);
    }

    /// <summary>The size of the `jpg-hdr` file for an image of <paramref name="size"/>: the size itself, or scaled down so that neither
    /// side is larger than <paramref name="maxSize"/>.</summary>
    public static (int W, int H) LimitedSize((int W, int H) size, int? maxSize)
    {
        if (maxSize is not int m || (size.W <= m && size.H <= m)) return size;
        int longSide = Math.Max(size.W, size.H);
        int w = Math.Max(1, (int)((long)size.W * m / longSide)), h = Math.Max(1, (int)((long)size.H * m / longSide));
        return (w, h);
    }
}
