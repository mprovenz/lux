using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;

namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>The result of refining one capture's depth.</summary>
/// <param name="Depth">The refined depth in mm on the grid of the depth cache, in the form <see cref="ExportState.FullDepth"/> takes.</param>
/// <param name="Window">The part of that grid the picture covers: only this part can change.</param>
/// <param name="Network">The network that was used (<see cref="IDepthNetwork.Id"/>).</param>
public sealed record RefinedDepth(float[] Depth, int W, int H, RectI Window, FusionReport Report, string Network, double NetworkSeconds, double FusionSeconds)
{
    /// <summary>The time of the check against the other cameras of the reference group; 0 when the capture has none.</summary>
    public double CrossViewSeconds { get; init; }

    /// <summary>One line for the user.</summary>
    public string Describe()
    {
        if (!Report.Applied) return $"refined depth: not applied ({Report.Reason})";
        double share = 100.0 * Report.Changed / Math.Max(Report.Pixels, 1), sky = 100.0 * Report.Sky / Math.Max(Report.Pixels, 1);
        if (Report.SkyOnly) return FormattableString.Invariant($"refined depth: only the sky was corrected, {sky:0.0} % set to the far limit ({Report.Reason})");
        return FormattableString.Invariant($"refined depth: {share:0.0} % of the depth map changed") + (Report.Sky > 0 ? FormattableString.Invariant($", {sky:0.0} % set to the far limit as sky") : "");
    }
}

/// <summary>
/// Refines the depth of a built capture with a depth network (<see cref="DepthPrior"/>, <see cref="DepthFusion"/>). The other cameras of
/// the reference group check the camera's depth first (<see cref="CrossViewCost"/>). A capture whose reference camera has no such group
/// (a telephoto capture) gets only its sky corrected.
/// <para>The refined depth is only ever put in <see cref="ExportState.FullDepth"/>, which the readers of depth for pictures use: the depth
/// effect, the depth-based parallax and stereo formats and the depth outputs. The registration, the fusion of the cameras and the full-size
/// render read the capture's own geometry and never see it.</para>
/// </summary>
public static class DepthRefiner
{
    /// <summary>Compute the refined depth of <paramref name="st"/> from its current depth. Nothing in the state is changed.</summary>
    /// <param name="progress">Receives the phase "refining depth": one unit per camera of the reference group, one per network run and
    /// one for the fusion.</param>
    public static RefinedDepth Compute(ExportState st, IDepthNetwork net, int threads, ProgressReporter? progress = null, CancellationToken cancel = default)
    {
        progress ??= ProgressReporter.None;
        var (lv, level, window) = Grid(st);
        var (depth, dw, dh) = st.FullDepth ?? throw new ExportRequestException("refined depth: this capture has no depth");
        var cache = DepthImageCache.FromFullDepth(depth, dw, dh, lv).Current;
        int w = window.X1 - window.X0, h = window.Y1 - window.Y0;
        if (Math.Min(w, h) < RefineConstants.NetworkSide) throw new ExportRequestException("refined depth: this capture is too small");

        // the capture's own picture on the grid of the depth: no depth effect, no saved settings
        var session = new ExportSession(st, new ExportRequest { Formats = new[] { ExportImageFormat.Jpeg }, Level = level, RenderThreads = threads, Cancel = cancel, UseSavedEdits = false }, null);
        var (_, rgba) = session.Rgba8();
        if (session.Grid.Size != (w, h)) throw new InvalidOperationException($"the picture is {session.Grid.Size.W}x{session.Grid.Size.H}, the depth window {w}x{h}");

        var z = new float[(long)w * h];
        for (int y = 0; y < h; y++) Array.Copy(cache.Data, cache.Offset + (long)(y + window.Y0) * cache.Stride + window.X0, z, (long)y * w, w);
        var rig = Rig.Of(st, session, lv, (w, h));

        progress.Begin(PhaseName, rig.Cameras + DepthPrior.Runs(w, h) + 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cost = rig.CrossView(st, session, z, w, h, threads, () => progress.Tick(), cancel);
        double tCross = sw.Elapsed.TotalSeconds; sw.Restart();
        int turn = RefineConstants.NetworkUpright ? Lux.Engine.Lri.CaptureMotion.HeldDegrees(st.Lri.Header) : 0;
        var prior = DepthPrior.Run(rgba, w, h, net, () => progress.Tick(), cancel, turn);
        double tNet = sw.Elapsed.TotalSeconds; sw.Restart();
        var (refined, report) = DepthFusion.Fuse(z, rgba, w, h, rig.FocalBaseline, prior, cost, cancel);
        double tFuse = sw.Elapsed.TotalSeconds;
        progress.Tick(); progress.End();

        // The camera's depth on the cache grid, as the cache samples it. The cache made from this image is the cache made from the camera's
        // depth, value for value, so a pixel the fusion does not rewrite keeps its exact depth.
        var full = ImageResample0.Run(new DepthImage(depth, dw, dh, dw, new RectI(0, 0, dw, dh)), cache.W, cache.H).Data;
        if (report.Applied)
            for (int y = 0; y < h; y++)
            {
                long o = (long)y * w, c = (long)(y + window.Y0) * cache.W + window.X0;
                for (int x = 0; x < w; x++) if (refined[o + x] != z[o + x]) full[c + x] = refined[o + x];
            }
        return new RefinedDepth(full, cache.W, cache.H, window, report, net.Id, tNet, tFuse) { CrossViewSeconds = tCross };
    }

    /// <summary>Refine the depth of <paramref name="st"/> in place: every reader of depth for pictures then uses the refined depth. Call it
    /// only on a state that has no saved depth edits applied, and that no other render shares.</summary>
    public static RefinedDepth Apply(ExportState st, IDepthNetwork net, int threads, ProgressReporter? progress = null, CancellationToken cancel = default)
    {
        if (st.EditedDepth is not null) throw new InvalidOperationException("saved depth edits are applied to this state");
        var r = Compute(st, net, threads, progress, cancel);
        if (r.Report.Applied) st.FullDepth = (r.Depth, r.W, r.H);
        return r;
    }

    /// <summary>The kind of a stored depth version that holds refined depth.</summary>
    public const string StoredKind = "refined";

    /// <summary>The refinement step of an export: refine the depth of the export's own state, unless saved depth edits are in use. Returns
    /// the note for the user.</summary>
    public static string ApplyForExport(ExportState st, IDepthNetwork net, ExportRequest req, ProgressReporter progress, Action<string>? log)
    {
        string note;
        if (st.EditedDepth is { } e)
            note = e.Kind == StoredKind
                ? $"refined depth: the saved depth version '{e.Variant}' is refined depth and is used as saved"
                : $"refined depth: not applied, because saved depth edits are in use (variant '{e.Variant}'); `--no-edits` refines the camera's depth instead";
        else
        {
            var r = Apply(st, net, req.RenderThreads, progress, req.Cancel);
            note = r.Describe() + FormattableString.Invariant($" (other cameras {r.CrossViewSeconds:0.0} s, network {r.NetworkSeconds:0.0} s, fusion {r.FusionSeconds:0.0} s)");
        }
        log?.Invoke(note);
        return note;
    }

    /// <summary>The progress phase of the refinement.</summary>
    public const string PhaseName = "refining depth";

    /// <summary>The level tables of the capture, the level of the depth cache and the picture's window on the cache grid.</summary>
    static (ExportLevels Levels, int Level, RectI Window) Grid(ExportState st)
    {
        var lv = Exporter.Resolve(st, new ExportRequest { Formats = Array.Empty<ExportImageFormat>() }).Window;
        int l = DepthEditor.EditLevel(lv);
        var (w, h) = lv.ExportDims[l]; var (ox, oy) = lv.Origins[l];
        return (lv, l, new RectI(ox, oy, ox + w, oy + h));
    }

    /// <summary>The reference group of a capture on the grid of the picture: the cameras with the same view as the reference camera.</summary>
    /// <param name="Others">The cameras of the group without the reference camera; empty when the reference camera is not in the group
    /// of wide cameras.</param>
    /// <param name="Widest">The widest baseline from the reference camera (mm).</param>
    sealed record Rig(ParallaxGeometry Geometry, IReadOnlyList<string> Others, double Widest)
    {
        /// <summary>Focal length (pixels of the picture) × the widest baseline of the reference group (mm).</summary>
        public double FocalBaseline => Geometry.FocalPx * Widest;

        /// <summary>The number of cameras that the cross-view cost renders; 0 when the capture gets none.</summary>
        public int Cameras => Others.Count >= RefineConstants.CrossViewMinCameras ? Others.Count + 1 : 0;

        public static Rig Of(ExportState st, ExportSession session, ExportLevels lv, (int W, int H) size)
        {
            var g = ParallaxGeometry.FromCapture(st.Lri, session.Registration(), (st.Frame.Width, st.Frame.Height), st.Dims[0], lv, size);
            double widest = 0; var others = new List<string>();
            var group = Wigglegram.SelectModules(st.Lri, 0, includeMono: false);
            if (group.Contains(g.Reference))
                foreach (var name in group.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (name == g.Reference) continue;
                    var (bx, by) = g.BaselineOf(name, g.Reference);
                    widest = Math.Max(widest, Math.Sqrt(bx * bx + by * by));
                    others.Add(name);
                }
            if (!(widest > 0)) { widest = RefineConstants.FallbackBaseline; others.Clear(); }
            return new Rig(g, others, widest);
        }

        /// <summary>The cross-view cost of the depth <paramref name="z"/> (mm, on the grid of the picture). Each camera of the group is
        /// rendered on its own and brought onto the grid of the picture, one or two at a time (<see cref="RefineConstants.CrossViewCamerasNow"/>). Null when the capture has too few cameras with the same view.</summary>
        public float[]? CrossView(ExportState st, ExportSession session, float[] z, int w, int h, int threads, Action tick, CancellationToken cancel)
        {
            if (Cameras == 0) return null;
            var lri = st.Lri; var api = session.Registration(); var g = Geometry;
            var exposure = ModuleRender.ExposureReference(lri, lri.ReferenceModule);
            var names = new List<string> { g.Reference }; names.AddRange(Others);
            var onGrid = new byte[names.Count][];
            Parallel.For(0, names.Count, new ParallelOptions { MaxDegreeOfParallelism = RefineConstants.CrossViewCamerasNow(threads), CancellationToken = cancel }, i =>
            {
                string name = names[i];
                var native = ModuleRender.ToRgba(ModuleRender.Render(lri, st.Colour, st.Wb, name, lri.Modules[name], exposure));
                onGrid[i] = ModuleGrid.ToExportGrid(native, g, ModuleGrid.CalibFor(lri, api, name), Affine.Identity).P;
                tick();
            });
            // the cameras are added in a fixed order, so the result does not depend on the number of threads
            var cost = new CrossViewCost(onGrid[0], DepthFusion.Disparity(z, w, h, FocalBaseline), w, h);
            for (int i = 1; i < names.Count; i++)
            {
                var (bx, by) = g.BaselineOf(names[i], g.Reference);
                cost.Add(onGrid[i], bx / Widest, by / Widest, cancel);
                onGrid[i] = null!;
            }
            return cost.Result();
        }
    }
}
