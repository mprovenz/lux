using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Session;

/// <summary>The 3D formats.</summary>
public enum SceneFormat
{
    /// <summary>`mesh-glb`: a textured mesh as a binary glTF file, a "3D photo".</summary>
    MeshGlb,
    /// <summary>`pointcloud-ply`: a colored point cloud as a binary PLY file.</summary>
    PointCloudPly,
}

/// <summary>The 3D formats of an export, as data.</summary>
public sealed record SceneRequest
{
    public IReadOnlyList<SceneFormat> Formats { get; init; } = Array.Empty<SceneFormat>();
    /// <summary>`mesh-glb`: the number of triangles after the simplification.</summary>
    public int MeshTriangles { get; init; } = 250_000;
    /// <summary>`mesh-glb`: true caps the depth at 4 times the subject distance (a relief with the background as a backdrop); false
    /// keeps the metric depth and leaves out content beyond 60 m.</summary>
    public bool MeshRelief { get; init; } = true;
    /// <summary>`pointcloud-ply`: every n-th pixel of the picture becomes a point (1, 2, 4 or 8).</summary>
    public int PointStep { get; init; } = 4;
}

/// <summary>
/// Writes the 3D formats of an export (<see cref="SceneRequest"/>) from the exported picture and the depth on the same grid. The grid is
/// the 13 MP picture, or a smaller export level when the request names one. The picture is not turned or cropped: the camera of the
/// file is the camera of the capture.
/// </summary>
public static class SceneExport
{
    /// <summary>Grid step of the mesh vertices, in pixels.</summary>
    public const int MeshStep = 4;
    /// <summary>Width of the texture of the mesh, in pixels.</summary>
    public const int TextureWidth = 4096;

    public static string NameOf(SceneFormat f) => f == SceneFormat.MeshGlb ? "mesh-glb" : "pointcloud-ply";
    public static bool TryParse(string name, out SceneFormat f)
    {
        if (name == "mesh-glb") { f = SceneFormat.MeshGlb; return true; }
        if (name == "pointcloud-ply") { f = SceneFormat.PointCloudPly; return true; }
        f = default; return false;
    }
    public static string Suffix(SceneFormat f) => f == SceneFormat.MeshGlb ? "_3d.glb" : "_cloud.ply";

    /// <summary>The session that gives the picture and the depth of a depth-based Lux format: the picture as recorded (no crop, no
    /// rotation), at the 13 MP level unless the request names a smaller one.</summary>
    internal static ExportSession SourceSession(ExportSession s, ExportRequest req, Action<string>? log)
    {
        int level = Math.Max(1, req.Level ?? 0);
        bool plain = req.Transform is null && req.Rotate == 0 && req.Size is null && req.Origin is null && (req.Level ?? 0) == level;
        return plain ? s : new ExportSession(s.State, req with { Level = level, Transform = null, Rotate = 0, Size = null, Origin = null }, log);
    }

    public static void Write(ExportRequest req, ExportSession session, string outDir, string stem, List<Exporter.Output> outputs, List<string> notes, Action<string>? log)
    {
        var r = req.Scene!;
        var s = SourceSession(session, req, log);
        var src = s.ParallaxSource();
        int refId = (int)s.State.Lri.Modules[s.State.Lri.ReferenceModule].Module.Id;
        var cam = Relief3D.CameraOf(src.Geometry, s.Registration().Pairs[refId].First.K);
        log?.Invoke(FormattableString.Invariant($"3D: camera of the {cam.W}x{cam.H} picture: f {cam.F:F2} px, center ({cam.Cx:F1}, {cam.Cy:F1})"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in r.Formats.Distinct())
        {
            req.Cancel.ThrowIfCancellationRequested();
            sw.Restart();
            string path = Path.Combine(outDir, stem + Suffix(f));
            if (f == SceneFormat.PointCloudPly)
            {
                var cloud = Relief3D.BuildCloud(src.Colour, src.Depth.V, cam, r.PointStep);
                Relief3D.WritePly(path, cloud, "Light L16 capture, one viewpoint. Meters. x right, y down, z forward from the camera");
                notes.Add(FormattableString.Invariant($"pointcloud-ply: {cloud.Count} points from every {r.PointStep}{(r.PointStep == 1 ? "st" : r.PointStep == 2 ? "nd" : "th")} pixel of the {cam.W}x{cam.H} picture; left out {100 * cloud.AtStep:0.0} % at depth steps, {100 * cloud.Far:0.0} % beyond {Relief3D.CloudFarMm / 1000:0} m and {100 * cloud.NoDepth:0.0} % without depth"));
            }
            else
            {
                var mesh = Relief3D.BuildMesh(src.Depth.V, cam, MeshStep, r.MeshTriangles, r.MeshRelief);
                if (mesh.Triangles == 0) throw new ExportRequestException("mesh-glb: the depth map of this capture gives no surface");
                int tw = Math.Min(TextureWidth, cam.W), th = Math.Max(1, (int)Math.Round((double)cam.H * tw / cam.W));
                var tex = RealStereo.Downscale(src.Colour, tw, th);
                using var jpg = new MemoryStream();
                JpegEncoder.Encode(jpg, tex.P, tex.W, tex.H, tex.W * 4, grayscale: false, new JpegEncoder { Quality = 90 });
                string depthNote = r.MeshRelief ? FormattableString.Invariant($"depth capped at {mesh.DepthLimitMm / 1000:0.0} m") : FormattableString.Invariant($"metric depth, content beyond {Relief3D.MeshFarMm / 1000:0} m left out");
                File.WriteAllBytes(path, Relief3D.EncodeGlb(mesh, jpg.ToArray(), "Lux", "One capture, one viewpoint: a relief, " + depthNote));
                notes.Add(FormattableString.Invariant($"mesh-glb: {mesh.Triangles} triangles and {mesh.Vertices} vertices (from {mesh.GridTriangles} on the grid), {depthNote}; it is a relief for a viewing angle of a few degrees"));
            }
            outputs.Add(new Exporter.Output(NameOf(f), path, new FileInfo(path).Length, sw.Elapsed.TotalSeconds));
            log?.Invoke($"{NameOf(f)} -> {Path.GetFileName(path)} in {sw.Elapsed.TotalSeconds:F1}s");
        }
    }
}
