namespace Lux.Engine.Pipeline.Export;

/// <summary>The still formats of Lux that Lumen does not have. Each is written from the same render as the JPG.</summary>
public enum StillFormat
{
    /// <summary>`tiff16`: the picture of the JPG at 16 bits per channel, in a TIFF file.</summary>
    Tiff16,
    /// <summary>`tiff-linear`: the picture before the tone curve, as floating-point values in linear ProPhoto RGB, in a TIFF file.</summary>
    TiffLinear,
    /// <summary>`jpg-hdr`: the JPG with a gain map that holds a brighter rendition for HDR displays.</summary>
    JpegHdr,
}

/// <summary>The color space of a `tiff16` file.</summary>
public enum TiffColorSpace
{
    Srgb,
    DisplayP3,
    AdobeRgb,
}

/// <summary>The compression of the pixel data in a TIFF file.</summary>
public enum TiffCompression
{
    /// <summary>Deflate (zlib) with a predictor. The smallest files.</summary>
    Deflate,
    /// <summary>LZW with a predictor.</summary>
    Lzw,
    /// <summary>No compression.</summary>
    None,
}

/// <summary>The sample type of a `tiff-linear` file.</summary>
public enum TiffFloatDepth
{
    /// <summary>32-bit floating point.</summary>
    Float32,
    /// <summary>16-bit floating point (half).</summary>
    Float16,
}

/// <summary>The settings of the two TIFF formats.</summary>
public sealed record TiffOptions
{
    /// <summary>The color space of `tiff16`. `tiff-linear` is always linear ProPhoto RGB.</summary>
    public TiffColorSpace ColorSpace { get; init; } = TiffColorSpace.Srgb;
    public TiffCompression Compression { get; init; } = TiffCompression.Deflate;
    /// <summary>The sample type of `tiff-linear`. `tiff16` is always 16-bit unsigned integer.</summary>
    public TiffFloatDepth LinearDepth { get; init; } = TiffFloatDepth.Float32;
}

/// <summary>The settings of the `jpg-hdr` format.</summary>
public sealed record GainMapOptions
{
    /// <summary>The gain map has 1 / <see cref="Scale"/> of the width and height of the picture: 4, 2 or 1.</summary>
    public int Scale { get; init; } = 4;
    /// <summary>True: one gain for each of red, green and blue. False: one gain for the luminance.</summary>
    public bool Rgb { get; init; } = true;
    /// <summary>The JPEG quality of the gain map, 1 to 100.</summary>
    public int Quality { get; init; } = 90;
    /// <summary>The largest width and height of the file in pixels. A larger picture is scaled down. Null = no limit.</summary>
    public int? MaxSize { get; init; }

    public GainMapOptions Validate()
    {
        if (Scale is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(Scale), "the gain map scale must be 4, 2 or 1");
        if (Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Quality), "the gain map quality must be between 1 and 100");
        if (MaxSize is int m && m < 16) throw new ArgumentOutOfRangeException(nameof(MaxSize), "the largest size must be at least 16 pixels");
        return this;
    }
}

/// <summary>The `--formats` names and the file names of the <see cref="StillFormat"/>s.</summary>
public static class StillFormats
{
    public static readonly StillFormat[] All = { StillFormat.Tiff16, StillFormat.TiffLinear, StillFormat.JpegHdr };

    /// <summary>The `--formats` name.</summary>
    public static string NameOf(StillFormat f) => f switch
    {
        StillFormat.Tiff16 => "tiff16",
        StillFormat.TiffLinear => "tiff-linear",
        StillFormat.JpegHdr => "jpg-hdr",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>The part of the file name after the stem. The three formats can be written beside each other and beside the JPG.</summary>
    public static string Suffix(StillFormat f) => f switch
    {
        StillFormat.Tiff16 => ".tif",
        StillFormat.TiffLinear => "_linear.tif",
        StillFormat.JpegHdr => "_hdr.jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static bool TryParse(string name, out StillFormat f)
    {
        switch (name.ToLowerInvariant())
        {
            case "tiff16": f = StillFormat.Tiff16; return true;
            case "tiff-linear": f = StillFormat.TiffLinear; return true;
            case "jpg-hdr" or "jpeg-hdr": f = StillFormat.JpegHdr; return true;
            default: f = default; return false;
        }
    }

    /// <summary>The `--tiff-cs` name of a color space.</summary>
    public static string NameOf(TiffColorSpace cs) => cs switch { TiffColorSpace.Srgb => "srgb", TiffColorSpace.DisplayP3 => "p3", _ => "adobergb" };

    public static bool TryParseColorSpace(string name, out TiffColorSpace cs)
    {
        switch (name.ToLowerInvariant())
        {
            case "srgb": cs = TiffColorSpace.Srgb; return true;
            case "p3": cs = TiffColorSpace.DisplayP3; return true;
            case "adobergb": cs = TiffColorSpace.AdobeRgb; return true;
            default: cs = default; return false;
        }
    }
}
