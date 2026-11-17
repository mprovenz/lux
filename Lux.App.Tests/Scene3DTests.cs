using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Lux.Engine.Pipeline.Export;
using Lux.Engine.Pipeline.Parallax;
using Lux.Engine.Session;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Tests of the 3D formats: the point cloud and the mesh from a picture and its depth. The geometry runs on a synthetic scene, a wall
/// with a box in front of it. The PLY and GLB files are read back with readers that are written here from the format specifications.
/// The export panel offers the two formats. A real capture is exported through <c>lux-light</c> at a small level when
/// <c>LUX_TEST_CAPTURES</c> names the capture folder.
/// </summary>
public sealed class Scene3DTests
{
    readonly ITestOutputHelper _out;
    public Scene3DTests(ITestOutputHelper output) { _out = output; }

    const int W = 400, H = 300;
    static readonly Relief3D.Camera Cam = new(F: 500, Cx: 205.5, Cy: 148.25, W: W, H: H);

    /// <summary>A wall at 4 m with a box at 1.5 m in the middle, and a strip of sky (100 m) at the top. The picture is a color ramp.
    /// The large box fills most of the middle of the picture, so it is the subject.</summary>
    static (Rgba Picture, float[] Depth) Scene(float wall = 4000f, bool largeBox = false)
    {
        var p = new Rgba(W, H); var d = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool box = largeBox ? x >= 130 && x < 270 && y >= 100 && y < 230 : x >= 150 && x < 250 && y >= 120 && y < 220;
                d[y * W + x] = y < 20 ? 100000f : box ? 1500f : wall;
                int o = (y * W + x) * 4; p.P[o] = (byte)(x * 255 / W); p.P[o + 1] = (byte)(y * 255 / H); p.P[o + 2] = box ? (byte)200 : (byte)40; p.P[o + 3] = 255;
            }
        return (p, d);
    }

    [Fact]
    public void The_camera_of_the_export_grid_follows_the_view_the_canvas_and_the_window()
    {
        // a view with f 3400 and center (2085, 1560), magnified by 2.5, a window that starts at (220, 150), a picture of half the window
        var g = new ParallaxGeometry { CanvasFactor = 2.5, ExportL0 = (8800, 6600), Origin0 = (220, 150), Size = (4400, 3300) };
        var c = Relief3D.CameraOf(g, new float[] { 3400, 0, 2085, 0, 3400, 1560, 0, 0, 1 });
        Assert.Equal(3400 * 2.5 / 2, c.F, 9);
        Assert.Equal((2085 * 2.5 - 220) / 2, c.Cx, 9);
        Assert.Equal((1560 * 2.5 - 150) / 2, c.Cy, 9);
        Assert.Equal((4400, 3300), (c.W, c.H));
    }

    [Fact]
    public void A_depth_step_is_a_jump_of_the_disparity_and_not_a_small_change_far_away()
    {
        var (_, d) = Scene();
        var m = Relief3D.StepMask(d, W, H, Cam.F);
        // 43.6 mm at f 500: disparity 14.5 px at 1.5 m and 5.5 px at 4 m, a jump of 9 px
        Assert.Equal(1, m[170 * W + 150]); Assert.Equal(1, m[170 * W + 148]); Assert.Equal(1, m[170 * W + 251]);
        Assert.Equal(0, m[170 * W + 146]); Assert.Equal(0, m[170 * W + 200]); Assert.Equal(0, m[60 * W + 60]);
        // a far wall with a step from 40 m to 44 m: 0.05 px of disparity, no step
        var far = new float[W * H];
        for (int i = 0; i < far.Length; i++) far[i] = i % W < 200 ? 40000f : 44000f;
        Assert.DoesNotContain((byte)1, Relief3D.StepMask(far, W, H, Cam.F));
    }

    [Fact]
    public void The_cloud_holds_the_points_in_meters_with_normals_and_colors_and_leaves_out_steps_and_the_far_range()
    {
        var (p, d) = Scene();
        var c = Relief3D.BuildCloud(p, d, Cam, 4);
        int grid = ((W - 1 + 3) / 4) * ((H - 1 + 3) / 4);
        _out.WriteLine($"{c.Count} points of {grid}; steps {c.AtStep:P1}, far {c.Far:P1}, no depth {c.NoDepth:P1}");
        Assert.Equal(c.Count * 3, c.Positions.Length); Assert.Equal(c.Count * 3, c.Normals.Length); Assert.Equal(c.Count * 3, c.Colors.Length);
        Assert.InRange(c.Far, 0.05, 0.08);                       // the sky rows
        Assert.InRange(c.AtStep, 0.005, 0.06);
        Assert.Equal(0, c.NoDepth);
        Assert.Equal(grid, c.Count + (int)Math.Round((c.Far + c.AtStep) * grid));
        bool sawBox = false;
        for (int i = 0; i < c.Count; i++)
        {
            float x = c.Positions[3 * i], y = c.Positions[3 * i + 1], z = c.Positions[3 * i + 2];
            Assert.True(z is 1.5f or 4f);
            // each point lies on the ray of a pixel of the grid
            double px = x / z * Cam.F + Cam.Cx, py = y / z * Cam.F + Cam.Cy;
            Assert.Equal(Math.Round(px), px, 3); Assert.Equal(Math.Round(py), py, 3);
            Assert.Equal(1, ((int)Math.Round(px)) % 4); Assert.Equal(1, ((int)Math.Round(py)) % 4);
            // the normal of a wall that faces the camera points back at the camera
            Assert.Equal(-1f, c.Normals[3 * i + 2], 3);
            if (z == 1.5f) { sawBox = true; Assert.Equal(200, c.Colors[3 * i + 2]); } else Assert.Equal(40, c.Colors[3 * i + 2]);
        }
        Assert.True(sawBox);
        Assert.True(Relief3D.BuildCloud(p, d, Cam, 1).Count > 14 * c.Count);
    }

    [Fact]
    public void A_ply_file_reads_back()
    {
        var (p, d) = Scene();
        var c = Relief3D.BuildCloud(p, d, Cam, 8);
        string path = Path.Combine(Paths.UiScratch, "scene3d-test.ply");
        Relief3D.WritePly(path, c, "a test");
        var f = File.ReadAllBytes(path);
        int end = f.AsSpan().IndexOf("end_header\n"u8) + 11;
        var header = Encoding.ASCII.GetString(f, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "ply", "format binary_little_endian 1.0", "comment a test", $"element vertex {c.Count}", "property float x", "property float y", "property float z",
                             "property float nx", "property float ny", "property float nz", "property uchar red", "property uchar green", "property uchar blue", "end_header" }, header);
        Assert.Equal(end + 27 * c.Count, f.Length);
        for (int i = 0; i < c.Count; i += 37)
        {
            int o = end + 27 * i;
            for (int k = 0; k < 3; k++)
            {
                Assert.Equal(c.Positions[3 * i + k], BinaryPrimitives.ReadSingleLittleEndian(f.AsSpan(o + 4 * k)));
                Assert.Equal(c.Normals[3 * i + k], BinaryPrimitives.ReadSingleLittleEndian(f.AsSpan(o + 12 + 4 * k)));
                Assert.Equal(c.Colors[3 * i + k], f[o + 24 + k]);
            }
        }
    }

    [Fact]
    public void The_simplifier_reaches_the_target_keeps_the_border_and_turns_no_triangle()
    {
        // a wavy sheet of 120 × 90 vertices
        int gw = 120, gh = 90; var pos = new double[gw * gh * 3]; var idx = new List<int>();
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) { int o = (j * gw + i) * 3; pos[o] = i * 10; pos[o + 1] = j * 10; pos[o + 2] = 2000 + 15 * Math.Sin(i * 0.2) * Math.Cos(j * 0.15); }
        for (int j = 0; j + 1 < gh; j++) for (int i = 0; i + 1 < gw; i++) { int a = j * gw + i, b = a + 1, c = a + gw, d = c + 1; idx.AddRange(new[] { a, c, b, b, c, d }); }
        int before = idx.Count / 3;
        var (sp, si) = QuadricSimplifier.Simplify(pos, idx.ToArray(), 3000);
        int after = si.Length / 3;
        _out.WriteLine($"{before} -> {after} triangles, {sp.Length / 3} vertices");
        Assert.InRange(after, 2000, 3000);
        Assert.True(si.Max() < sp.Length / 3 && si.Min() == 0);
        // every border vertex of the sheet is still there, at its place
        var kept = new HashSet<(double, double)>(); for (int v = 0; v < sp.Length; v += 3) kept.Add((sp[v], sp[v + 1]));
        for (int i = 0; i < gw; i++) { Assert.Contains((i * 10.0, 0.0), kept); Assert.Contains((i * 10.0, (gh - 1) * 10.0), kept); }
        for (int j = 0; j < gh; j++) { Assert.Contains((0.0, j * 10.0), kept); Assert.Contains(((gw - 1) * 10.0, j * 10.0), kept); }
        // all triangles still face the same way, and the surface stays close to the sheet
        double area = 0;
        for (int t = 0; t < si.Length; t += 3)
        {
            int a = si[t] * 3, b = si[t + 1] * 3, c = si[t + 2] * 3;
            double nz = (sp[b] - sp[a]) * (sp[c + 1] - sp[a + 1]) - (sp[b + 1] - sp[a + 1]) * (sp[c] - sp[a]);
            Assert.True(nz < 0);
            area += -nz / 2;
        }
        Assert.Equal((gw - 1) * 10.0 * (gh - 1) * 10.0, area, 3);
        for (int v = 0; v < sp.Length; v += 3) Assert.InRange(sp[v + 2] - (2000 + 15 * Math.Sin(sp[v] / 10 * 0.2) * Math.Cos(sp[v + 1] / 10 * 0.15)), -4, 4);
        // the same input gives the same output
        var (sp2, si2) = QuadricSimplifier.Simplify(pos, idx.ToArray(), 3000);
        Assert.Equal(si, si2); Assert.Equal(sp.Select(BitConverter.DoubleToInt64Bits), sp2.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void The_mesh_has_no_triangle_across_a_depth_step_and_caps_the_depth_of_a_relief()
    {
        var (_, d) = Scene(wall: 20000f, largeBox: true);
        var relief = Relief3D.BuildMesh(d, Cam, 4, 2000, relief: true);
        var metric = Relief3D.BuildMesh(d, Cam, 4, 2000, relief: false);
        _out.WriteLine($"relief {relief.Triangles} triangles (grid {relief.GridTriangles}), limit {relief.DepthLimitMm} mm; metric {metric.Triangles}, limit {metric.DepthLimitMm} mm");
        // the subject is the box at 1.5 m: the relief caps the depth at 8 m, the least cap
        Assert.Equal(8000f, relief.DepthLimitMm);
        Assert.Equal(60000f, metric.DepthLimitMm);
        foreach (var (m, wallZ, sky) in new[] { (relief, -8f, true), (metric, -20f, false) })
        {
            Assert.InRange(m.Triangles, 500, 2600);
            Assert.True(m.Triangles < m.GridTriangles);
            Assert.Equal(m.Vertices * 3, m.Normals.Length); Assert.Equal(m.Vertices * 2, m.Uvs.Length);
            bool box = false, wall = false;
            for (int v = 0; v < m.Vertices; v++)
            {
                float z = m.Positions[3 * v + 2];
                Assert.True(MathF.Abs(z + 1.5f) < 1e-3f || MathF.Abs(z - wallZ) < 1e-3f, $"z {z}");     // glTF axes: the scene is at negative z
                box |= MathF.Abs(z + 1.5f) < 1e-3f; wall |= MathF.Abs(z - wallZ) < 1e-3f;
                float nx = m.Normals[3 * v], ny = m.Normals[3 * v + 1], nz = m.Normals[3 * v + 2];
                Assert.Equal(1f, MathF.Sqrt(nx * nx + ny * ny + nz * nz), 5);
                Assert.True(nz > 0.9f);                                                                    // toward the viewer
                Assert.InRange(m.Uvs[2 * v], 0f, 1f); Assert.InRange(m.Uvs[2 * v + 1], 0f, 1f);
            }
            Assert.True(box && wall);
            // no triangle joins the box and the wall
            for (int t = 0; t < m.Indices.Length; t += 3)
            {
                float a = m.Positions[3 * m.Indices[t] + 2], b = m.Positions[3 * m.Indices[t + 1] + 2], c = m.Positions[3 * m.Indices[t + 2] + 2];
                Assert.True(MathF.Abs(a - b) < 1e-3f && MathF.Abs(a - c) < 1e-3f);
            }
            // the sky rows: a backdrop at the cap in the relief, left out of the metric mesh
            float top = float.MinValue; for (int v = 0; v < m.Vertices; v++) top = MathF.Max(top, m.Positions[3 * v + 1]);
            float topOfWall = (float)((Cam.Cy - 22) / Cam.F * -wallZ);
            if (sky) Assert.True(top > topOfWall + 0.05f); else Assert.True(top <= topOfWall + 0.05f);
        }
    }

    [Fact]
    public void A_glb_file_has_a_valid_container_and_buffers_that_match_its_accessors()
    {
        var (p, d) = Scene();
        var mesh = Relief3D.BuildMesh(d, Cam, 4, 1500, relief: true);
        using var jpg = new MemoryStream();
        JpegEncoder.Encode(jpg, p.P, W, H, W * 4, false, new JpegEncoder { Quality = 90 });
        var glb = Relief3D.EncodeGlb(mesh, jpg.ToArray(), "Lux", "a \"test\"");
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb)); Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        int jsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)));
        Assert.Equal(0, jsLen % 4);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsLen));
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal("a \"test\"", root.GetProperty("asset").GetProperty("extras").GetProperty("note").GetString());
        int binLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(20 + jsLen));
        Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(24 + jsLen)));
        Assert.Equal(glb.Length, 28 + jsLen + binLen);
        Assert.Equal(binLen, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());
        var acc = root.GetProperty("accessors"); var views = root.GetProperty("bufferViews");
        int[] size = { 12, 12, 8, 4 }; int[] count = { mesh.Vertices, mesh.Vertices, mesh.Vertices, mesh.Indices.Length };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(count[i], acc[i].GetProperty("count").GetInt32());
            var v = views[acc[i].GetProperty("bufferView").GetInt32()];
            Assert.Equal(count[i] * size[i], v.GetProperty("byteLength").GetInt32());
            Assert.Equal(0, v.GetProperty("byteOffset").GetInt32() % 4);
            Assert.True(v.GetProperty("byteOffset").GetInt32() + v.GetProperty("byteLength").GetInt32() <= binLen);
        }
        // the position bounds are the bounds of the data, and every index names a vertex
        var min = acc[0].GetProperty("min"); var max = acc[0].GetProperty("max");
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(Enumerable.Range(0, mesh.Vertices).Min(v => mesh.Positions[3 * v + k]), min[k].GetSingle());
            Assert.Equal(Enumerable.Range(0, mesh.Vertices).Max(v => mesh.Positions[3 * v + k]), max[k].GetSingle());
        }
        Assert.True(mesh.Indices.Max() < mesh.Vertices);
        // the texture is the JPEG picture, inside the buffer
        var img = views[root.GetProperty("images")[0].GetProperty("bufferView").GetInt32()];
        int at = 28 + jsLen + img.GetProperty("byteOffset").GetInt32();
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, glb[at..(at + 2)]);
        Assert.Equal("image/jpeg", root.GetProperty("images")[0].GetProperty("mimeType").GetString());
        Assert.Equal("KHR_materials_unlit", root.GetProperty("extensionsUsed")[0].GetString());
    }

    [Fact]
    public void Format_names_files_and_requests()
    {
        Assert.True(SceneExport.TryParse("mesh-glb", out var m)); Assert.Equal(SceneFormat.MeshGlb, m);
        Assert.True(SceneExport.TryParse("pointcloud-ply", out var c)); Assert.Equal(SceneFormat.PointCloudPly, c);
        Assert.False(SceneExport.TryParse("mesh-obj", out _));
        Assert.Equal("_3d.glb", SceneExport.Suffix(SceneFormat.MeshGlb)); Assert.Equal("_cloud.ply", SceneExport.Suffix(SceneFormat.PointCloudPly));
        var defaults = new SceneRequest();
        Assert.Equal((250_000, true, 4), (defaults.MeshTriangles, defaults.MeshRelief, defaults.PointStep));
        // the 3D formats read the depth; a request without them needs what it needed before
        var plain = new ExportRequest { Formats = Array.Empty<ExportImageFormat>() };
        Assert.Null(plain.Scene); Assert.False(plain.NeedsDepth);
        Assert.True((plain with { Scene = new SceneRequest { Formats = new[] { SceneFormat.PointCloudPly } } }).NeedsDepth);
        var o = new ExportOptions { Formats = new[] { "jpg", "mesh-glb", "pointcloud-ply" } };
        Lux.Library.Core.ExportJobs.Validate(o);
        var req = Lux.Library.Core.ExportJobs.BuildRequest(o, "/tmp/out", "c1");
        Assert.Equal(new[] { SceneFormat.MeshGlb, SceneFormat.PointCloudPly }, req.Scene!.Formats);
        Assert.Null(Lux.Library.Core.ExportJobs.BuildRequest(new ExportOptions(), "/tmp/out", "c1").Scene);
        Assert.Equal("3D photo (GLB)", Lux.Library.Core.ExportJobs.DisplayName("mesh-glb"));
        Assert.Equal("Point cloud (PLY)", Lux.Library.Core.ExportJobs.DisplayName("pointcloud-ply"));
        var back = Assert.IsType<ExportJobRequest>(Lux.Library.Core.JobRequests.Deserialize(JobKind.Export, Lux.Library.Core.JobRequests.Serialize(new ExportJobRequest("c1", o))));
        Assert.Equal(o.Formats, back.Options.Formats);
    }

    [AvaloniaFact]
    public async Task Export_panel_offers_the_3d_formats()
    {
        await using var h = await Harness.CreateAsync();
        await h.MockupSelectionAsync();
        h.Library.OpenExport();
        var panel = h.Library.ExportPanel!;
        var glb = panel.Formats.Single(f => f.Key == "mesh-glb"); var ply = panel.Formats.Single(f => f.Key == "pointcloud-ply");
        Assert.Equal("3D photo (GLB)", glb.Name); Assert.Equal("Point cloud (PLY)", ply.Name);
        Assert.False(glb.IsOn); Assert.False(ply.IsOn);
        Assert.Contains("Relief", glb.Note);
        foreach (var f in panel.Formats) f.IsOn = f.Key is "mesh-glb" or "pointcloud-ply";
        await h.SettleAsync();
        Assert.True(panel.CanAdd);
        Assert.StartsWith("3D photo (GLB) + Point cloud (PLY)", panel.Describe());
        Assert.EndsWith("_3d.glb", panel.FileNamePreview);
        h.Shot("export-panel-3d");
        panel.AddToQueueCommand.Execute(null);
        await h.SettleAsync(() => panel.LastOptions != null);
        Assert.Equal(new[] { "mesh-glb", "pointcloud-ply" }, panel.LastOptions!.Formats);
    }

    static int Cli(string args, out string log)
    {
        var psi = new ProcessStartInfo(Paths.LuxLight, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["LUX_FORBID_DEFAULT_APP_DATA"] = "1";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        log = o + err.Result;
        return p.ExitCode;
    }

    /// <summary>A real capture at a small level: both files are written, the point count in the PLY header matches the file, and the GLB
    /// container is complete.</summary>
    [Fact]
    public void A_real_capture_gives_a_mesh_and_a_point_cloud()
    {
        string lri = Path.Combine(Paths.Captures, "L16_00442.lri");
        if (Paths.Captures.Length == 0 || !File.Exists(lri)) { _out.WriteLine("LUX_TEST_CAPTURES has no L16_00442.lri: skipped"); return; }
        string dir = Path.Combine(Paths.UiScratch, "scene3d");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Assert.Equal(0, Cli($"convert \"{lri}\" --formats mesh-glb,pointcloud-ply --level 3 --mesh-triangles 20000 --pc-step 2 --no-cache -o \"{dir}\"", out string log));
        _out.WriteLine(log);
        Assert.Contains("one capture gives a relief", log);
        Assert.Contains("--depth-refine gives cleaner results", log);
        var glb = File.ReadAllBytes(Path.Combine(dir, "L16_00442_3d.glb"));
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        int jsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsLen));
        int tris = doc.RootElement.GetProperty("accessors")[3].GetProperty("count").GetInt32() / 3;
        Assert.InRange(tris, 5000, 26000);
        var ply = File.ReadAllBytes(Path.Combine(dir, "L16_00442_cloud.ply"));
        int end = ply.AsSpan().IndexOf("end_header\n"u8) + 11;
        string header = Encoding.ASCII.GetString(ply, 0, end);
        int n = int.Parse(header.Split('\n').First(l => l.StartsWith("element vertex ", StringComparison.Ordinal))[15..]);
        Assert.Equal(end + 27 * n, ply.Length);
        Assert.True(n > 20000);
        Assert.Contains($"pointcloud-ply: {n} points", log);
        Assert.Equal(2, Cli($"convert \"{lri}\" --formats jpg --pc-step 4 --no-cache -o \"{dir}\"", out string bad));
        Assert.Contains("It applies to: pointcloud-ply", bad);
        Assert.Equal(2, Cli($"convert \"{lri}\" --formats mesh-glb --mesh-depth deep --no-cache -o \"{dir}\"", out bad));
        Assert.Contains("--mesh-depth must be relief or metric", bad);
    }
}
