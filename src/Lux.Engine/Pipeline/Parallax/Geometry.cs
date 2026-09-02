using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>The geometry a depth-image-based render needs, all of it derived from the capture rather than assumed.
/// Spec `a-parallax-experiments.md` §2 for how <see cref="FocalPx"/> was derived and then measured against the real
/// modules (joint gain 0.9920 / 0.9885 / 1.0072 over three captures).</summary>
public sealed class ParallaxGeometry
{
    /// <summary>Focal length in pixels **of the export image actually produced**, i.e. of
    /// <see cref="ParallaxSource.Colour"/>. Not the native 3377.96 px: the export canvas is the module frame magnified
    /// by the group focal ratio (<c>ExportWindow.CanvasFactor</c>, 70/28 = 2.5 snapped to 10432/4160 = 2.507692) and
    /// then cropped and resampled to the requested size. See <see cref="Describe"/> for the chain.</summary>
    public double FocalPx;
    /// <summary>Focal in native module pixels — the aligned view camera's K[0] (<c>api.Pairs[ref].First</c>).</summary>
    public double FocalNative;
    public double CanvasFactor;            // canvas.W / sensor.W
    public double ExportToCanvas;          // canvas px per export-output px
    public (int W, int H) Sensor, Canvas, ExportL0, Size;
    public (int X, int Y) Origin0;
    /// <summary>A-group module optical centres in mm, `−(Rᵀ·t)` from the factory extrinsics. Key = module name.</summary>
    public Dictionary<string, (double X, double Y, double Z)> Centres = new();
    public string Reference = "0";
    /// <summary>The modules a virtual path should be laid out over: the reference module's own optical group, colour
    /// only. The B and C groups are in <see cref="Centres"/> too but they frame a different part of the scene, and the
    /// monochrome module is excluded for the same reason the wigglegram excludes it.</summary>
    public List<string> PathModules = new();

    public string Describe() =>
        $"sensor {Sensor.W}x{Sensor.H}  canvas {Canvas.W}x{Canvas.H} (x{CanvasFactor:F6})  exportL0 {ExportL0.W}x{ExportL0.H}@({Origin0.X},{Origin0.Y})  "
      + $"size {Size.W}x{Size.H}\n  f_native {FocalNative:F2} px -> f_canvas {FocalNative * CanvasFactor:F2} px -> f_export {FocalPx:F2} px "
      + $"(export/canvas = {1.0 / ExportToCanvas:F6})";

    /// <summary>Baseline of module <paramref name="name"/> relative to the reference module, in millimetres, in the
    /// image's own axes. The extrinsics' Y already runs with the image rows — established by measurement (fitting the
    /// x and y parallax gains apart gives gy = +0.84/+0.88 with this convention and −0.87/−0.83 with the other, spec
    /// `a-parallax-experiments.md` §2.2), so no sign is applied.</summary>
    public (double X, double Y) BaselineOf(string name, string refName)
    {
        var a = Centres[name]; var b = Centres[refName];
        return (a.X - b.X, a.Y - b.Y);
    }

    /// <summary>The geometry of an export rendered at <paramref name="size"/> from the window <paramref name="win"/>:
    /// the focal chain above plus the A-group centres. <paramref name="api"/> only needs <c>Setup()</c> to have run
    /// (the ctor pairs); the level-0 registration state is the same object and serves unchanged.</summary>
    public static ParallaxGeometry FromCapture(LriFile lri, StereoAsyncApi api, (int W, int H) sensor, (int W, int H) canvas,
                                               ExportLevels win, (int W, int H) size)
    {
        var g = new ParallaxGeometry
        {
            Sensor = sensor, Canvas = canvas, ExportL0 = win.ExportDims[0], Origin0 = win.Origins[0], Size = size,
            CanvasFactor = (double)canvas.W / sensor.W,
            ExportToCanvas = (double)win.ExportDims[0].W / size.W,
            Reference = lri.ReferenceModule,
        };
        // The aligned view camera's focal, in native module pixels. `AlignedCalibrationScan` scales the view camera onto
        // the canvas for the tele path; the reference pair is unscaled, so K[0] is in module pixels.
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;
        g.FocalNative = api.Pairs[refId].First.K[0];
        g.FocalPx = g.FocalNative * g.CanvasFactor / g.ExportToCanvas;
        int refGroup = Wigglegram.Group(refId);
        foreach (var (name, mref) in lri.Modules)
        {
            var c = Wigglegram.CameraCentre(lri, mref.Module.Id);
            if (c is null) continue;
            g.Centres[name] = c.Value;
            if (Wigglegram.Group((int)mref.Module.Id) == refGroup && !Wigglegram.IsMono(mref.Module)) g.PathModules.Add(name);
        }
        g.PathModules.Sort(StringComparer.Ordinal);
        return g;
    }
}

/// <summary>A parallax source: the display-rendered export image and the metric depth aligned to it, with the
/// geometry that relates a virtual camera translation to pixel motion. Built once per capture from the export
/// state `convert` already holds — never from a second pipeline build.</summary>
public sealed class ParallaxSource
{
    public required Rgba Colour;
    public required Plane Depth;      // metric millimetres, pixel-aligned to Colour
    public required ParallaxGeometry Geometry;
    public required string Stem;

    public int W => Colour.W;
    public int H => Colour.H;

    /// <summary>Downscale, keeping the geometry consistent (f scales with the image).</summary>
    public ParallaxSource Scaled(int longEdge)
    {
        if (longEdge <= 0 || Math.Max(W, H) <= longEdge) return this;
        double k = (double)longEdge / Math.Max(W, H);
        int ow = Math.Max(1, (int)Math.Round(W * k)), oh = Math.Max(1, (int)Math.Round(H * k));
        var G = Geometry;
        var g = new ParallaxGeometry
        {
            FocalPx = G.FocalPx * ow / (double)W, FocalNative = G.FocalNative, CanvasFactor = G.CanvasFactor,
            ExportToCanvas = G.ExportToCanvas * W / (double)ow, Sensor = G.Sensor, Canvas = G.Canvas,
            ExportL0 = G.ExportL0, Origin0 = G.Origin0, Size = (ow, oh), Centres = G.Centres, Reference = G.Reference,
            PathModules = G.PathModules,
        };
        return new ParallaxSource { Colour = Colour.Resize(ow, oh), Depth = Depth.ResizeNearest(ow, oh), Geometry = g, Stem = Stem };
    }
}
