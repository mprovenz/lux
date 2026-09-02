using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.Parallax;

/// <summary>The affine part of a module's registration onto the export grid: <c>v ↦ v + C(v)</c>. Identity in
/// production; the harness's `parallax-calibrate` fits one to show that it *is* identity.</summary>
public readonly record struct Affine(double B11, double B12, double T1, double B21, double B22, double T2)
{
    public static readonly Affine Identity = new(0, 0, 0, 0, 0, 0);
    public (double X, double Y) Delta(double x, double y) => (B11 * x + B12 * y + T1, B21 * x + B22 * y + T2);
    public override string ToString() =>
        $"[{1 + B11:F6} {B12:F6} {T1:F2} | {B21:F6} {1 + B22:F6} {T2:F2}]";
}

/// <summary>
/// How a real module frame is brought onto the export grid.
///
/// A module frame and the export image are not in the same coordinate frame. The export canvas is the canonical
/// camera magnified by the group focal ratio (10432/4160) and cropped by the header's view preferences, and every
/// module has its own factory rotation, principal point and distortion on top. The mapping is therefore taken from the
/// pipeline itself — <see cref="CalibFor"/> builds the same <c>AlignedCalib</c> the reference cache uses, with the
/// reference's view camera kept for every module. Placing a module frame by scale and crop alone left an 11–21 px
/// RMS residual that no global affine could absorb (spec `a-parallax-experiments.md` §2.3); going through
/// <see cref="AlignedCalib"/> removes it exactly, because it is the pipeline's own rectification rather than a model
/// of it.
/// </summary>
public static class ModuleGrid
{
    /// <summary>The registration state, set up but not run: <c>Setup()</c> alone produces the ctor pairs
    /// <c>api+0x3a8[id]</c> for every reference-group camera (slots, poses and crops — no images, no bundle
    /// adjustment), which is all the geometry needs and takes well under a second. A level-0 export state already
    /// holds one (the full run leaves the reference-group pairs untouched), so this is only for callers without it.</summary>
    public static StereoAsyncApi SetupApi(LriFile lri, Action<string>? log = null)
    {
        var api = new StereoAsyncApi { Lri = lri, Log = log };
        api.Setup();
        return api;
    }

    /// <summary>The warp that takes the **canonical (aligned) camera** of the capture to a given module's raw frame —
    /// the same object <c>ExportBuild</c> builds for the reference module, with the reference's view camera kept for
    /// every module so that all of them land on one grid.</summary>
    public static AlignedCalib CalibFor(LriFile lri, StereoAsyncApi api, string name)
    {
        int refId = (int)lri.Modules[lri.ReferenceModule].Module.Id;
        int id = (int)lri.Modules[name].Module.Id;
        if (!api.Pairs.TryGetValue(id, out var pair)) throw new InvalidOperationException($"no ctor calibration pair for module {name} (id {id})");
        var view = api.Pairs[refId].First;         // the reference's canonical camera, shared by every module
        var dist = StereoImageBuilder.DistortionOf(lri, name);
        return AlignedCalib.Build(view, pair.Second, 1f, 1f, 1f, 1f, dist.PpX, dist.PpY, dist.Poly, dist.Pix, dist.Pix);
    }

    /// <summary>
    /// Resample a native module frame onto the export grid: export pixel → canvas pixel → canonical camera pixel →
    /// (through <paramref name="calib"/>) module pixel.
    ///
    /// The chain is the export renderer's own, read back: the export level-L grid is the canvas halved L times with
    /// the window origin added, and the canvas is the canonical camera magnified by
    /// <c>ExportWindow.CanvasFactor</c> = 70/28 snapped to 10432/4160. <paramref name="corr"/> is an optional residual
    /// affine; it should be identity and is kept so that the harness can *show* it is.
    /// </summary>
    public static Rgba ToExportGrid(Rgba module, ParallaxGeometry g, AlignedCalib calib, Affine corr)
    {
        double eX = (double)g.ExportL0.W / g.Size.W, eY = (double)g.ExportL0.H / g.Size.H;
        double k = 1.0 / g.CanvasFactor;
        var o = new Rgba(g.Size.W, g.Size.H);
        int mw = module.W, mh = module.H;
        Parallel.For(0, g.Size.H, y =>
        {
            for (int x = 0; x < g.Size.W; x++)
            {
                var (ddx, ddy) = corr.Delta(x, y);
                double canvasX = g.Origin0.X + (x + ddx) * eX, canvasY = g.Origin0.Y + (y + ddy) * eY;
                var (mx, my) = calib.Map((float)(canvasX * k), (float)(canvasY * k), 0);
                long d = ((long)y * g.Size.W + x) * 4;
                int x0 = (int)Math.Floor(mx), y0 = (int)Math.Floor(my);
                if (x0 < 0 || y0 < 0 || x0 + 1 >= mw || y0 + 1 >= mh) { o.P[d + 3] = 0; continue; }
                double tx = mx - x0, ty = my - y0;
                long pa = ((long)y0 * mw + x0) * 4, pb = pa + 4, pc = pa + (long)mw * 4, pdd = pc + 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = (module.P[pa + c] * (1 - tx) + module.P[pb + c] * tx) * (1 - ty)
                             + (module.P[pc + c] * (1 - tx) + module.P[pdd + c] * tx) * ty;
                    o.P[d + c] = (byte)Math.Clamp(v + 0.5, 0, 255);
                }
                o.P[d + 3] = 255;
            }
        });
        return o;
    }
}
