using System.Globalization;

namespace Lux.Cli;

/// <summary>
/// The global `--flag` set. A flag that configures one format or command carries that prefix (`--dng-tone`,
/// `--jpeg-quality`, `--lens-ev`, `--parallax-size`, `--isp-level`); the shared ones (`--level`, `--size`, `--rotate`, the Exif
/// trio) keep their bare names and document their scope. A flag that does not parse is rejected, not silently
/// reinterpreted — see <see cref="Errors"/>.
/// </summary>
public sealed class Options
{
    // ---- OUTPUT ---------------------------------------------------------------------------------------------
    public string? OutDirectory { get; private set; }
    public string? OutFile { get; private set; }
    public int Threads { get; private set; } = Environment.ProcessorCount;
    /// <summary>convert: output list, e.g. "dng,jpg,hdr,ppm,jpg+depth,depth" (default dng,jpg).</summary>
    public string? Formats { get; private set; }
    /// <summary>convert: shorthand for adding hdr to --formats.</summary>
    /// <summary>convert: alias for adding `depth` to --formats.</summary>

    // ---- GRID (convert / depth) -----------------------------------------------------------------------------
    public int Level { get; private set; }                  // export level, 0 = full size
    public (int W, int H)? Size { get; private set; }
    public (int X, int Y)? Origin { get; private set; }

    // ---- SHARED ADJUSTMENTS ---------------------------------------------------------------------------------
    public int? Rotate { get; private set; }
    public float? FNumber { get; private set; }
    public int? Iso { get; private set; }
    public int? Focal { get; private set; }

    // ---- DNG ------------------------------------------------------------------------------------------------
    public int? DngCs { get; private set; }
    public string? DngTone { get; private set; }
    public int? DngComp { get; private set; }

    // ---- JPEG -----------------------------------------------------------------------------------------------
    public int? JpegCs { get; private set; }
    public int? JpegQuality { get; private set; }
    public int? JpegSub { get; private set; }
    public bool JpegV2 { get; private set; }
    public DateTime? JpegModify { get; private set; }
    public string? JpegComment { get; private set; }
    public string? JpegSoftware { get; private set; }

    // ---- HDR ------------------------------------------------------------------------------------------------
    public int? HdrCs { get; private set; }
    // (no --ppm-cs: measured inert — cp.dll's own fmt-1 exports at property 1, 3 and 4 are the same 2 433 639
    //  bytes, md5-identical, because the `(fmt|4)==4` gate means a PPM never reads the output tuning.)

    // ---- LENS-FRAMES (convert) ------------------------------------------------------------------------------
    public int LensQuality { get; private set; } = 92;
    public float LensEv { get; private set; } = 0.95f;
    public int LensLevel { get; private set; }
    public int LensProfile { get; private set; } = 3;
    public string[]? LensModules { get; private set; }
    public string? LensStack { get; private set; }

    // ---- PARALLAX (convert) ---------------------------------------------------------------------------------
    public string? ParallaxFormat { get; private set; }
    public int? ParallaxSize { get; private set; }
    public int? ParallaxMs { get; private set; }
    public int? ParallaxFrames { get; private set; }
    public string? ParallaxLoop { get; private set; }
    public string? ParallaxFill { get; private set; }
    public string? ParallaxPath { get; private set; }
    public double? ParallaxBaseline { get; private set; }
    public string? ParallaxConverge { get; private set; }
    public (int X, int Y)? ParallaxConvergeAt { get; private set; }
    public double? ParallaxIpd { get; private set; }
    public string? ParallaxAnaglyph { get; private set; }
    public double? ParallaxFocus { get; private set; }
    public (int X, int Y)? ParallaxFocusAt { get; private set; }
    public double? ParallaxAperture { get; private set; }
    public int? ParallaxLayers { get; private set; }
    public (double M1, double M2)? ParallaxRack { get; private set; }
    public ((int X, int Y) A, (int X, int Y) B)? ParallaxRackAt { get; private set; }
    public double? ParallaxSubject { get; private set; }
    public (int X, int Y)? ParallaxSubjectAt { get; private set; }
    public double? ParallaxDz { get; private set; }
    public (double X, double Y)? ParallaxT { get; private set; }
    public int? ParallaxQuality { get; private set; }
    public int? ParallaxCrf { get; private set; }
    public string? ParallaxOrder { get; private set; }
    public (int X, int Y, int W, int H)? ParallaxPivot { get; private set; }

    // ---- ISP ------------------------------------------------------------------------------------------------
    public int IspLevel { get; private set; }
    public int Profile { get; private set; } = 3;           // isp/isp-run: CIAPI RendererProfile (Lumen.exe passes 3)

    // ---- PULL -----------------------------------------------------------------------------------------------
    public string[]? Extensions { get; private set; }
    public string? Glob { get; private set; }
    public DateTimeOffset? Since { get; private set; }
    public bool Overwrite { get; private set; }
    public bool ListOnly { get; private set; }

    /// <summary>The flags actually present on the command line, so a command can reject one that names something
    /// it does not write (`--jpeg-quality` with `--formats dng`) or one that belongs to a different command.</summary>
    public HashSet<string> Given { get; } = new(StringComparer.Ordinal);

    /// <summary>Fatal parse errors — a value that does not parse. Reported before dispatch.</summary>
    public List<string> Errors { get; } = new();

    public bool Has(params string[] flags) => flags.Any(Given.Contains);

    static (int X, int Y) Pt(string s)
    {
        var p = s.Split(',');
        if (p.Length != 2) throw new FormatException($"'{s}' is not an x,y point");
        return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
    }

    public static Options Parse(string[] args, out List<string> positionals)
    {
        var o = new Options();
        positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            string Need(string what) => Next() ?? throw new FormatException($"{a} needs {what}");
            if (a.StartsWith('-') && a.Length > 1) o.Given.Add(a);
            try
            {
                switch (a)
                {
                    // ---- output
                    case "-o" or "--out-directory": o.OutDirectory = Need("a directory"); break;
                    case "--out-file": o.OutFile = Need("a path"); break;
                    case "-j" or "--threads": o.Threads = Math.Max(1, int.Parse(Need("a count"), CultureInfo.InvariantCulture)); break;
                    case "--formats" or "--format":
                    {   // a comma list may be written with spaces (`dng, jpg, ppm`): absorb the tokens the shell split off
                        var list = Need("a list, e.g. dng,jpg");
                        while (i + 1 < args.Length && (list.EndsWith(',') || args[i + 1].StartsWith(','))) list += args[++i];
                        o.Formats = list; break;
                    }

                    // ---- grid
                    case "--level": o.Level = int.Parse(Need("a level"), CultureInfo.InvariantCulture); break;
                    case "--size": { var p = Need("w,h").Split(','); o.Size = (int.Parse(p[0]), int.Parse(p[1])); break; }
                    case "--origin": { var p = Need("x,y").Split(','); o.Origin = (int.Parse(p[0]), int.Parse(p[1])); break; }

                    // ---- shared adjustments
                    case "--rotate":
                    {
                        string r = Need("0, 90, 180 or 270");
                        if (r is not ("0" or "90" or "180" or "270")) { o.Errors.Add("--rotate must be 0, 90, 180 or 270"); break; }
                        o.Rotate = int.Parse(r, CultureInfo.InvariantCulture); break;
                    }
                    case "--fnum": o.FNumber = float.Parse(Need("an f-number"), CultureInfo.InvariantCulture); break;
                    case "--iso": o.Iso = int.Parse(Need("an ISO"), CultureInfo.InvariantCulture); break;
                    case "--focal": o.Focal = int.Parse(Need("a focal length in mm"), CultureInfo.InvariantCulture); break;

                    // ---- dng
                    case "--dng-cs": o.DngCs = int.Parse(Need("a colour-space property"), CultureInfo.InvariantCulture); break;
                    case "--dng-tone": o.DngTone = Need("a tone-mapping profile"); break;
                    case "--dng-comp": o.DngComp = int.Parse(Need("0 or 1"), CultureInfo.InvariantCulture); break;

                    // ---- jpeg
                    case "--jpeg-cs": o.JpegCs = int.Parse(Need("a colour-space property"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-quality": o.JpegQuality = int.Parse(Need("1-100"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-sub": o.JpegSub = int.Parse(Need("0, 1 or 2"), CultureInfo.InvariantCulture); break;
                    case "--jpeg-v2": o.JpegV2 = true; break;
                    case "--jpeg-modify": o.JpegModify = DateTime.Parse(Need("YYYY-MM-DDTHH:MM:SS"), CultureInfo.InvariantCulture, DateTimeStyles.None); break;
                    case "--jpeg-comment": o.JpegComment = Need("text"); break;
                    case "--jpeg-software": o.JpegSoftware = Need("text"); break;

                    // ---- hdr
                    case "--hdr-cs": o.HdrCs = int.Parse(Need("a colour-space property"), CultureInfo.InvariantCulture); break;

                    // ---- lens-frames
                    case "--lens-quality": o.LensQuality = Math.Clamp(int.Parse(Need("1-100"), CultureInfo.InvariantCulture), 1, 100); break;
                    case "--lens-ev": o.LensEv = float.Parse(Need("stops"), CultureInfo.InvariantCulture); break;
                    case "--lens-level": o.LensLevel = int.Parse(Need("a config level"), CultureInfo.InvariantCulture); break;
                    case "--lens-profile": o.LensProfile = int.Parse(Need("0-3"), CultureInfo.InvariantCulture); break;
                    case "--lens-modules": o.LensModules = Need("a module list").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--lens-stack": o.LensStack = Need("'all' or an index"); break;

                    // ---- parallax
                    case "--parallax-format": o.ParallaxFormat = Need("gif, webp, avif or apng").ToLowerInvariant(); break;
                    case "--parallax-size": o.ParallaxSize = int.Parse(Need("a long edge in px"), CultureInfo.InvariantCulture); break;
                    case "--parallax-ms": o.ParallaxMs = int.Parse(Need("milliseconds"), CultureInfo.InvariantCulture); break;
                    case "--parallax-frames": o.ParallaxFrames = int.Parse(Need("a frame count"), CultureInfo.InvariantCulture); break;
                    case "--parallax-loop": o.ParallaxLoop = Need("pingpong or forward").ToLowerInvariant(); break;
                    case "--parallax-fill": o.ParallaxFill = Need("donors, inpaint or none").ToLowerInvariant(); break;
                    case "--parallax-path": o.ParallaxPath = Need("sweep, arc or line").ToLowerInvariant(); break;
                    case "--parallax-baseline": o.ParallaxBaseline = double.Parse(Need("millimetres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-converge": o.ParallaxConverge = Need("metres, auto or none").ToLowerInvariant(); break;
                    case "--parallax-converge-at": o.ParallaxConvergeAt = Pt(Need("x,y")); break;
                    case "--parallax-ipd": o.ParallaxIpd = double.Parse(Need("millimetres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-anaglyph": o.ParallaxAnaglyph = Need("dubois, colour or grey").ToLowerInvariant(); break;
                    case "--parallax-focus": o.ParallaxFocus = double.Parse(Need("metres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-focus-at": o.ParallaxFocusAt = Pt(Need("x,y")); break;
                    case "--parallax-aperture": o.ParallaxAperture = double.Parse(Need("millimetres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-layers": o.ParallaxLayers = int.Parse(Need("a layer count"), CultureInfo.InvariantCulture); break;
                    case "--parallax-rack": { var p = Need("m1,m2").Split(','); o.ParallaxRack = (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)); break; }
                    case "--parallax-rack-at": { var p = Need("x1,y1;x2,y2").Split(';'); o.ParallaxRackAt = (Pt(p[0]), Pt(p[1])); break; }
                    case "--parallax-subject": o.ParallaxSubject = double.Parse(Need("metres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-subject-at": o.ParallaxSubjectAt = Pt(Need("x,y")); break;
                    case "--parallax-dz": o.ParallaxDz = double.Parse(Need("millimetres"), CultureInfo.InvariantCulture); break;
                    case "--parallax-t": { var p = Need("tx,ty").Split(','); o.ParallaxT = (double.Parse(p[0], CultureInfo.InvariantCulture), p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : 0); break; }
                    case "--parallax-quality": o.ParallaxQuality = int.Parse(Need("0-100"), CultureInfo.InvariantCulture); break;
                    case "--parallax-crf": o.ParallaxCrf = int.Parse(Need("0-63"), CultureInfo.InvariantCulture); break;
                    case "--parallax-order": o.ParallaxOrder = Need("sweep, label or a module list"); break;
                    case "--parallax-pivot": { var p = Need("x,y,w,h").Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray(); if (p.Length != 4) throw new FormatException("--parallax-pivot needs x,y,w,h"); o.ParallaxPivot = (p[0], p[1], p[2], p[3]); break; }

                    // ---- isp / isp-run
                    case "--isp-level": o.IspLevel = int.Parse(Need("a config level"), CultureInfo.InvariantCulture); break;
                    case "--profile": o.Profile = int.Parse(Need("0-3"), CultureInfo.InvariantCulture); break;

                    // ---- pull
                    case "--ext": o.Extensions = Need("a list").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(e => e.StartsWith('.') ? e : "." + e).ToArray(); break;
                    case "--glob": o.Glob = Need("a pattern"); break;
                    case "--since": o.Since = DateTimeOffset.Parse(Need("a date"), CultureInfo.InvariantCulture); break;
                    case "--overwrite": o.Overwrite = true; break;
                    case "--list": o.ListOnly = true; break;

                    default:
                        if (a.StartsWith('-')) o.Errors.Add($"unknown option '{a}' — `lux-light --help` lists every flag");
                        else positionals.Add(a);
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException or ArgumentNullException)
            {
                o.Errors.Add($"{a}: {ex.Message}");
            }
        }
        return o;
    }
}
