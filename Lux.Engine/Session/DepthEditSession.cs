using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.Dof.Edit;
using Lux.Engine.Pipeline.Export;

namespace Lux.Engine.Session;

/// <summary>
/// A depth editor on a built capture: the depth cache filled from the capture's full-frame depth, the editor on the renderer's level
/// tables, and its color images from the capture's own PipelineCache (<see cref="DepthEditColour"/>). The capture must have been built
/// at level 0 (<see cref="ExportBuild.Build"/> with maxLevel 0), which is what produces the depth.
/// </summary>
public sealed class DepthEditSession
{
    public ExportState State { get; }
    public ExportLevels Levels { get; }
    public DepthImageCache Cache { get; }
    public DepthImage Source { get; }
    public DepthEditor Editor { get; }
    public DepthEditColour Colour { get; }

    public DepthEditSession(ExportState st)
    {
        State = st;
        Levels = Exporter.Resolve(st, new ExportRequest { Formats = Array.Empty<ExportImageFormat>() }).Window;
        var (d, dw, dh) = st.FullDepth ?? throw new InvalidOperationException("this capture has no full-frame depth (build it at level 0)");
        Cache = DepthImageCache.FromFullDepth(d, dw, dh, Levels);
        Source = new DepthImage(d, dw, dh, dw, new RectI(0, 0, dw, dh));
        Editor = DepthEditor.Create(Cache, Source, Levels, st.Cache.LevelDims);
        Colour = new DepthEditColour(st.Cache, st.Frame, st.Colour, st.Lri.LumenNeutral, Levels);
        Colour.Attach(Editor);
    }

    /// <summary>Build the capture and open an editor on it.</summary>
    public static DepthEditSession Open(string lriPath, Action<string>? log = null, int threads = 1)
        => new(ExportBuild.Build(lriPath, 0, log, threads));
}
