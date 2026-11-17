using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// 3D geometry from one capture: a point cloud and a triangle mesh, made from the exported picture and its depth map. They are not
/// part of Lumen.
/// <para>The exported picture is the view of one pinhole camera without distortion (<see cref="Camera"/>), and the depth of a pixel is
/// its distance along the camera axis in millimeters. A pixel (x, y) with depth z is the point ((x − cx)·z/f, (y − cy)·z/f, z).</para>
/// <para>One capture sees the scene from one place. The result is a relief: correct from the camera's position, with nothing behind
/// the objects. It looks right inside a small viewing angle, about 0.5 to 3 degrees, and shows holes and stretched edges outside it.</para>
/// <para>A depth step is a jump of the disparity between neighbors that is larger than 4 pixels and larger than 8 % of the disparity,
/// for a baseline of 43.6 mm. The depth map has no true values along a step, so the cloud leaves those points out and the mesh has
/// no triangle across one.</para>
/// </summary>
public static class Relief3D
{
    /// <summary>The baseline of the disparity that the depth step rule uses, in mm.</summary>
    public const double StepBaselineMm = 43.6;
    /// <summary>The cloud leaves out points from this depth on, in mm: far ground, far buildings and the sky.</summary>
    public const float CloudFarMm = 30000f;
    /// <summary>The metric mesh leaves out content from this depth on, in mm.</summary>
    public const float MeshFarMm = 60000f;

    /// <summary>The pinhole camera of an exported picture: focal length and principal point in pixels, and the size.</summary>
    public readonly record struct Camera(double F, double Cx, double Cy, int W, int H);

    /// <summary>The camera of the picture of a parallax source. <paramref name="viewK"/> is the camera matrix of the reference lens's
    /// aligned view. The export grid is that view, magnified by the canvas factor, cut by the window and resampled:
    /// f = K[0]·factor/e and c = (K[2..]·factor − origin)/e, with e the canvas pixels for each export pixel.</summary>
    public static Camera CameraOf(ParallaxGeometry g, float[] viewK)
    {
        double ex = (double)g.ExportL0.W / g.Size.W, ey = (double)g.ExportL0.H / g.Size.H;
        return new Camera(viewK[0] * g.CanvasFactor / ex, (viewK[2] * g.CanvasFactor - g.Origin0.X) / ex, (viewK[5] * g.CanvasFactor - g.Origin0.Y) / ey, g.Size.W, g.Size.H);
    }

    static bool Valid(float z) => float.IsFinite(z) && z > 0f;

    /// <summary>1 for each pixel within <paramref name="radius"/> pixels of a depth step.</summary>
    public static byte[] StepMask(float[] depth, int w, int h, double focalPx, int radius = 2)
    {
        var d = new float[w * h];
        float fb = (float)(focalPx * StepBaselineMm);
        for (int i = 0; i < d.Length; i++) d[i] = fb / MathF.Max(Valid(depth[i]) ? depth[i] : 0f, 1f);
        // the largest and the smallest disparity in the window, rows first and then columns
        var mx = new float[w * h]; var mn = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = float.NegativeInfinity, b = float.PositiveInfinity;
                for (int k = Math.Max(0, x - radius); k <= Math.Min(w - 1, x + radius); k++) { float v = d[y * w + k]; if (v > a) a = v; if (v < b) b = v; }
                mx[y * w + x] = a; mn[y * w + x] = b;
            }
        });
        var o = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = float.NegativeInfinity, b = float.PositiveInfinity;
                for (int k = Math.Max(0, y - radius); k <= Math.Min(h - 1, y + radius); k++) { float v = mx[k * w + x]; if (v > a) a = v; v = mn[k * w + x]; if (v < b) b = v; }
                o[y * w + x] = (byte)(a - b > MathF.Max(4f, 0.08f * a) ? 1 : 0);
            }
        });
        return o;
    }

    // ------------------------------------------------------------------------------------------------ point cloud

    /// <summary>A point cloud: positions in meters (x right, y down, z forward), unit normals toward the camera, and colors.</summary>
    public sealed class Cloud
    {
        public float[] Positions = Array.Empty<float>(), Normals = Array.Empty<float>();
        public byte[] Colors = Array.Empty<byte>();
        public int Count => Positions.Length / 3;
        /// <summary>Shares of the grid points that were left out, 0 to 1: no depth, at a depth step, and far.</summary>
        public double NoDepth, AtStep, Far;
    }

    static void Point(in Camera c, float[] depth, int x, int y, out double X, out double Y, out double Z)
    {
        Z = depth[y * c.W + x]; X = (x - c.Cx) / c.F * Z; Y = (y - c.Cy) / c.F * Z;
    }

    /// <summary>The cloud of every <paramref name="step"/>-th pixel. Points without depth, at a depth step, or at
    /// <see cref="CloudFarMm"/> and beyond are left out.</summary>
    public static Cloud BuildCloud(Rgba picture, float[] depth, Camera cam, int step)
    {
        int w = cam.W, h = cam.H;
        var steps = StepMask(depth, w, h, cam.F);
        int off = step == 1 ? 0 : 1;
        var pos = new List<float>(); var nrm = new List<float>(); var col = new List<byte>();
        long total = 0, noDepth = 0, atStep = 0, far = 0;
        for (int y = off; y < h; y += step)
            for (int x = off; x < w; x += step)
            {
                total++;
                float z = depth[y * w + x];
                if (!Valid(z)) { noDepth++; continue; }
                if (steps[y * w + x] != 0) { atStep++; continue; }
                if (z >= CloudFarMm) { far++; continue; }
                Point(cam, depth, x, y, out double X, out double Y, out double Z);
                // the normal from the neighbors in the row and in the column, turned toward the camera
                Point(cam, depth, Math.Min(w - 1, x + 1), y, out double ax, out double ay, out double az); Point(cam, depth, Math.Max(0, x - 1), y, out double bx, out double by, out double bz);
                Point(cam, depth, x, Math.Min(h - 1, y + 1), out double cx, out double cy, out double cz); Point(cam, depth, x, Math.Max(0, y - 1), out double dx, out double dy, out double dz);
                double ux = ax - bx, uy = ay - by, uz = az - bz, vx = cx - dx, vy = cy - dy, vz = cz - dz;
                double nx = vy * uz - vz * uy, ny = vz * ux - vx * uz, nz = vx * uy - vy * ux, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 1e-12) { nx /= l; ny /= l; nz /= l; } else { nx = 0; ny = 0; nz = -1; }
                if (nx * X + ny * Y + nz * Z > 0) { nx = -nx; ny = -ny; nz = -nz; }
                pos.Add((float)(X / 1000)); pos.Add((float)(Y / 1000)); pos.Add((float)(Z / 1000));
                nrm.Add((float)nx); nrm.Add((float)ny); nrm.Add((float)nz);
                long p = ((long)y * w + x) * 4;
                col.Add(picture.P[p]); col.Add(picture.P[p + 1]); col.Add(picture.P[p + 2]);
            }
        return new Cloud { Positions = pos.ToArray(), Normals = nrm.ToArray(), Colors = col.ToArray(), NoDepth = (double)noDepth / total, AtStep = (double)atStep / total, Far = (double)far / total };
    }

    /// <summary>Write a cloud as a binary little-endian PLY file: x, y, z, nx, ny, nz as 32-bit floats and red, green, blue as bytes,
    /// 27 bytes for each point.</summary>
    public static void WritePly(string path, Cloud c, string comment)
    {
        using var fs = File.Create(path);
        var header = "ply\nformat binary_little_endian 1.0\ncomment " + comment + "\n" + string.Create(CultureInfo.InvariantCulture, $"element vertex {c.Count}\n")
                   + "property float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\n"
                   + "property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n";
        fs.Write(Encoding.ASCII.GetBytes(header));
        var buf = new byte[27 * 4096]; int n = 0;
        for (int i = 0; i < c.Count; i++)
        {
            int o = n * 27;
            for (int k = 0; k < 3; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o + 4 * k), c.Positions[3 * i + k]);
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o + 12 + 4 * k), c.Normals[3 * i + k]);
                buf[o + 24 + k] = c.Colors[3 * i + k];
            }
            if (++n == 4096) { fs.Write(buf, 0, n * 27); n = 0; }
        }
        if (n > 0) fs.Write(buf, 0, n * 27);
    }

    // ------------------------------------------------------------------------------------------------ mesh

    /// <summary>A textured mesh: positions in meters in glTF axes (x right, y up, z toward the viewer), unit normals, texture
    /// coordinates with the origin at the top left, and three vertex numbers for each triangle.</summary>
    public sealed class Mesh
    {
        public float[] Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>();
        public uint[] Indices = Array.Empty<uint>();
        public int Vertices => Positions.Length / 3;
        public int Triangles => Indices.Length / 3;
        /// <summary>Triangles of the grid before the simplification, and the depth limit that was used, in mm.</summary>
        public int GridTriangles; public float DepthLimitMm;
    }

    /// <summary>The distance of the subject, in mm: the median depth of the middle of the picture, kept between the 5th and the 50th
    /// percentile of the whole picture.</summary>
    public static float SubjectDepth(float[] depth, int w, int h)
    {
        var mid = new List<float>(); var all = new List<float>();
        int x0 = (int)(w * 0.3), x1 = (int)(w * 0.7), y0 = (int)(h * 0.3), y1 = (int)(h * 0.7);
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                float z = depth[y * w + x];
                if (!Valid(z) || z >= 99000f) continue;
                all.Add(z);
                if (x >= x0 && x < x1 && y >= y0 && y < y1) mid.Add(z);
            }
        if (all.Count == 0) return 0f;
        all.Sort(); mid.Sort();
        float p5 = all[(int)(0.05 * (all.Count - 1))], p50 = all[(int)(0.5 * (all.Count - 1))];
        float m = mid.Count > 0 ? mid[mid.Count / 2] : p50;
        return Math.Clamp(m, p5, p50);
    }

    /// <summary>The mesh of a picture and its depth: one vertex for each block of <paramref name="step"/> pixels, two triangles for
    /// each grid cell, no triangle across a depth step, then simplified to about <paramref name="targetTriangles"/> triangles. With
    /// <paramref name="relief"/> the depth is capped at 4 times the subject distance (at least 8 m), so that a far background stays
    /// close behind the subject as a backdrop. Without it, the depth is metric and content beyond <see cref="MeshFarMm"/> is left out.</summary>
    public static Mesh BuildMesh(float[] depth, Camera cam, int step, int targetTriangles, bool relief)
    {
        int w = cam.W, h = cam.H;
        float cap = relief ? Math.Min(Math.Max(4f * SubjectDepth(depth, w, h), 8000f), MeshFarMm) : MeshFarMm;
        int gw = (w - step / 2 + step - 1) / step, gh = (h - step / 2 + step - 1) / step;
        var z = new double[gw * gh]; var px = new double[gw * gh * 3];
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                int x = step / 2 + i * step, y = step / 2 + j * step; float v = depth[y * w + x];
                double zz = !Valid(v) ? 0 : relief ? Math.Min(v, cap) : v < cap ? v : 0;
                int o = j * gw + i; z[o] = zz;
                px[o * 3] = (x - cam.Cx) / cam.F * zz; px[o * 3 + 1] = (y - cam.Cy) / cam.F * zz; px[o * 3 + 2] = zz;
            }
        double fb = cam.F * StepBaselineMm, minCos = WineCrt.Cos(89.5 * Math.PI / 180.0);
        var map = new int[gw * gh]; Array.Fill(map, -1);
        var pos = new List<double>(); var idx = new List<int>();
        void Triangle(int a, int b, int c)
        {
            if (z[a] <= 0 || z[b] <= 0 || z[c] <= 0) return;
            double da = fb / Math.Max(z[a], 1), db = fb / Math.Max(z[b], 1), dc = fb / Math.Max(z[c], 1);
            double dmax = Math.Max(da, Math.Max(db, dc)), dmin = Math.Min(da, Math.Min(db, dc));
            if (dmax - dmin > Math.Max(4.0, 0.08 * dmax)) return;
            // a triangle that the camera sees edge-on has no usable shape
            double e1x = px[b * 3] - px[a * 3], e1y = px[b * 3 + 1] - px[a * 3 + 1], e1z = px[b * 3 + 2] - px[a * 3 + 2];
            double e2x = px[c * 3] - px[a * 3], e2y = px[c * 3 + 1] - px[a * 3 + 1], e2z = px[c * 3 + 2] - px[a * 3 + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            double cx = (px[a * 3] + px[b * 3] + px[c * 3]) / 3, cy = (px[a * 3 + 1] + px[b * 3 + 1] + px[c * 3 + 1]) / 3, cz = (px[a * 3 + 2] + px[b * 3 + 2] + px[c * 3 + 2]) / 3;
            double cos = Math.Abs(nx * cx + ny * cy + nz * cz) / Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz) * Math.Sqrt(cx * cx + cy * cy + cz * cz), 1e-12);
            if (cos < minCos) return;
            foreach (int v in new[] { a, b, c })
            {
                if (map[v] < 0) { map[v] = pos.Count / 3; pos.Add(px[v * 3]); pos.Add(px[v * 3 + 1]); pos.Add(px[v * 3 + 2]); }
                idx.Add(map[v]);
            }
        }
        for (int j = 0; j + 1 < gh; j++)
            for (int i = 0; i + 1 < gw; i++)
            {
                int a = j * gw + i, b = a + 1, c = a + gw, d = c + 1;
                Triangle(a, c, b); Triangle(b, c, d);
            }
        int gridTriangles = idx.Count / 3;
        var (sp, si) = gridTriangles > targetTriangles ? QuadricSimplifier.Simplify(pos.ToArray(), idx.ToArray(), targetTriangles) : (pos.ToArray(), idx.ToArray());

        // vertex normals: the sum of the triangle normals, weighted by area, turned toward the camera
        int nv = sp.Length / 3;
        var n = new double[nv * 3];
        for (int t = 0; t < si.Length; t += 3)
        {
            int a = si[t] * 3, b = si[t + 1] * 3, c = si[t + 2] * 3;
            double e1x = sp[b] - sp[a], e1y = sp[b + 1] - sp[a + 1], e1z = sp[b + 2] - sp[a + 2], e2x = sp[c] - sp[a], e2y = sp[c + 1] - sp[a + 1], e2z = sp[c + 2] - sp[a + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            foreach (int v in new[] { a, b, c }) { n[v] += nx; n[v + 1] += ny; n[v + 2] += nz; }
        }
        var m = new Mesh { Positions = new float[nv * 3], Normals = new float[nv * 3], Uvs = new float[nv * 2], Indices = new uint[si.Length], GridTriangles = gridTriangles, DepthLimitMm = cap };
        for (int v = 0; v < nv; v++)
        {
            double X = sp[v * 3], Y = sp[v * 3 + 1], Z = sp[v * 3 + 2];
            double nx = n[v * 3], ny = n[v * 3 + 1], nz = n[v * 3 + 2], l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(l > 1e-9)) { nx = -X; ny = -Y; nz = -Z; l = Math.Sqrt(nx * nx + ny * ny + nz * nz); }
            nx /= l; ny /= l; nz /= l;
            if (nx * X + ny * Y + nz * Z > 0) { nx = -nx; ny = -ny; nz = -nz; }
            // unit length in 32-bit floats: normalize once more after the conversion
            float fx = (float)nx, fy = (float)(-ny), fz = (float)(-nz); float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
            m.Normals[v * 3] = fx / fl; m.Normals[v * 3 + 1] = fy / fl; m.Normals[v * 3 + 2] = fz / fl;
            m.Positions[v * 3] = (float)(X / 1000); m.Positions[v * 3 + 1] = (float)(-Y / 1000); m.Positions[v * 3 + 2] = (float)(-Z / 1000);
            // the texture coordinate is the place of the vertex in the picture: the texture is the picture itself
            m.Uvs[v * 2] = (float)((cam.F * X / Z + cam.Cx + 0.5) / w); m.Uvs[v * 2 + 1] = (float)((cam.F * Y / Z + cam.Cy + 0.5) / h);
        }
        for (int i = 0; i < si.Length; i++) m.Indices[i] = (uint)si[i];
        return m;
    }

    /// <summary>The bytes of a binary glTF 2.0 file (GLB) with one mesh and its JPEG texture. The material is unlit: the texture
    /// is a photo and already holds the light of the scene.</summary>
    public static byte[] EncodeGlb(Mesh mesh, byte[] jpeg, string generator, string note)
    {
        using var bin = new MemoryStream();
        var views = new List<string>();
        int View(ReadOnlySpan<byte> data, int target)
        {
            long off = bin.Length; bin.Write(data);
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add(string.Create(CultureInfo.InvariantCulture, $"{{\"buffer\":0,\"byteOffset\":{off},\"byteLength\":{data.Length}{(target != 0 ? $",\"target\":{target}" : "")}}}"));
            return views.Count - 1;
        }
        static byte[] Bytes<T>(T[] a) where T : struct { var b = new byte[System.Runtime.InteropServices.Marshal.SizeOf<T>() * a.Length]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue }; var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < mesh.Positions.Length; i++) { int k = i % 3; float v = mesh.Positions[i]; if (v < min[k]) min[k] = v; if (v > max[k]) max[k] = v; }
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        int vp = View(Bytes(mesh.Positions), 34962), vn = View(Bytes(mesh.Normals), 34962), vt = View(Bytes(mesh.Uvs), 34962), vi = View(Bytes(mesh.Indices), 34963), vj = View(jpeg, 0);
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string json =
            $"{{\"asset\":{{\"version\":\"2.0\",\"generator\":\"{Esc(generator)}\",\"extras\":{{\"units\":\"meters\",\"note\":\"{Esc(note)}\"}}}},\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}]," +
            "\"nodes\":[{\"mesh\":0,\"name\":\"capture\"}],\"meshes\":[{\"name\":\"relief\",\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0,\"mode\":4}]}]," +
            "\"accessors\":[" +
            $"{{\"bufferView\":{vp},\"componentType\":5126,\"count\":{mesh.Vertices},\"type\":\"VEC3\",\"min\":[{F(min[0])},{F(min[1])},{F(min[2])}],\"max\":[{F(max[0])},{F(max[1])},{F(max[2])}]}}," +
            $"{{\"bufferView\":{vn},\"componentType\":5126,\"count\":{mesh.Vertices},\"type\":\"VEC3\"}}," +
            $"{{\"bufferView\":{vt},\"componentType\":5126,\"count\":{mesh.Vertices},\"type\":\"VEC2\"}}," +
            $"{{\"bufferView\":{vi},\"componentType\":5125,\"count\":{mesh.Indices.Length},\"type\":\"SCALAR\"}}]," +
            $"\"bufferViews\":[{string.Join(",", views)}],\"buffers\":[{{\"byteLength\":{bin.Length}}}]," +
            $"\"images\":[{{\"bufferView\":{vj},\"mimeType\":\"image/jpeg\"}}],\"textures\":[{{\"source\":0,\"sampler\":0}}]," +
            "\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}]," +
            "\"materials\":[{\"name\":\"photo\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0,\"roughnessFactor\":1},\"doubleSided\":true,\"extensions\":{\"KHR_materials_unlit\":{}}}]," +
            "\"extensionsUsed\":[\"KHR_materials_unlit\"]}";
        var js = Encoding.UTF8.GetBytes(json);
        int jsLen = (js.Length + 3) & ~3; long binLen = bin.Length;
        var o = new byte[12 + 8 + jsLen + 8 + binLen];
        BinaryPrimitives.WriteUInt32LittleEndian(o, 0x46546C67); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), (uint)o.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), (uint)jsLen); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16), 0x4E4F534A);
        js.CopyTo(o, 20); for (int i = js.Length; i < jsLen; i++) o[20 + i] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(20 + jsLen), (uint)binLen); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(24 + jsLen), 0x004E4942);
        bin.ToArray().CopyTo(o, 28 + jsLen);
        return o;
    }
}
