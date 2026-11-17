namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Mesh simplification with quadric error metrics, after Garland and Heckbert, in the iterative form of Sven Forstmann's Fast Quadric
/// Mesh Simplification (MIT license). Each vertex holds the sum of the squared distances to the planes of its triangles as a 4×4
/// symmetric matrix. An edge collapses to the point with the smallest summed error. A pass collapses each edge whose error is below a
/// threshold, and the threshold grows from pass to pass until the mesh has the target number of triangles. A collapse that would turn
/// a triangle over is not made, and a vertex on an open border does not move, so the outline and the cuts of the mesh stay as they are.
/// <para>The passes run in a fixed order with IEEE arithmetic only, so the result is the same on every processor.</para>
/// </summary>
public static class QuadricSimplifier
{
    struct Tri { public int V0, V1, V2; public double E0, E1, E2, E3; public bool Deleted, Dirty; public double Nx, Ny, Nz; }
    struct Vert { public double X, Y, Z; public int Start, Count; public bool Border; }
    struct Ref { public int Tri, Corner; }

    /// <summary>Simplify a triangle mesh. <paramref name="positions"/> holds x, y, z for each vertex and <paramref name="indices"/> three
    /// vertex numbers for each triangle. Returns the new positions and indices; no vertex is left without a triangle.</summary>
    public static (double[] Positions, int[] Indices) Simplify(double[] positions, int[] indices, int targetTriangles, int aggressiveness = 7)
    {
        int nv = positions.Length / 3, nt = indices.Length / 3;
        var v = new Vert[nv]; var q = new double[nv * 10];
        for (int i = 0; i < nv; i++) { v[i].X = positions[3 * i]; v[i].Y = positions[3 * i + 1]; v[i].Z = positions[3 * i + 2]; }
        var t = new List<Tri>(nt);
        for (int i = 0; i < nt; i++) t.Add(new Tri { V0 = indices[3 * i], V1 = indices[3 * i + 1], V2 = indices[3 * i + 2] });
        var tris = t.ToArray(); int triCount = tris.Length;
        var refs = new List<Ref>();
        var del0 = new List<bool>(); var del1 = new List<bool>();
        int deleted = 0, startCount = triCount;

        static int Corner(ref Tri tr, int k) => k == 0 ? tr.V0 : k == 1 ? tr.V1 : tr.V2;
        static void SetCorner(ref Tri tr, int k, int id) { if (k == 0) tr.V0 = id; else if (k == 1) tr.V1 = id; else tr.V2 = id; }

        double Det(double[] m, int a11, int a12, int a13, int a21, int a22, int a23, int a31, int a32, int a33) =>
            m[a11] * m[a22] * m[a33] + m[a13] * m[a21] * m[a32] + m[a12] * m[a23] * m[a31] - m[a13] * m[a22] * m[a31] - m[a11] * m[a23] * m[a32] - m[a12] * m[a21] * m[a33];
        static double VertexError(double[] m, double x, double y, double z) =>
            m[0] * x * x + 2 * m[1] * x * y + 2 * m[2] * x * z + 2 * m[3] * x + m[4] * y * y + 2 * m[5] * y * z + 2 * m[6] * y + m[7] * z * z + 2 * m[8] * z + m[9];

        var sum = new double[10];
        // the error of the collapse of an edge, and the point that it collapses to
        double EdgeError(int a, int b, out double px, out double py, out double pz)
        {
            for (int k = 0; k < 10; k++) sum[k] = q[a * 10 + k] + q[b * 10 + k];
            bool border = v[a].Border && v[b].Border;
            double det = Det(sum, 0, 1, 2, 1, 4, 5, 2, 5, 7);
            if (det != 0 && !border)
            {
                px = -1 / det * Det(sum, 1, 2, 3, 4, 5, 6, 5, 7, 8); py = 1 / det * Det(sum, 0, 2, 3, 1, 5, 6, 2, 7, 8); pz = -1 / det * Det(sum, 0, 1, 3, 1, 4, 6, 2, 5, 8);
                return VertexError(sum, px, py, pz);
            }
            double mx = (v[a].X + v[b].X) / 2, my = (v[a].Y + v[b].Y) / 2, mz = (v[a].Z + v[b].Z) / 2;
            double e1 = VertexError(sum, v[a].X, v[a].Y, v[a].Z), e2 = VertexError(sum, v[b].X, v[b].Y, v[b].Z), e3 = VertexError(sum, mx, my, mz);
            double e = Math.Min(e1, Math.Min(e2, e3));
            if (e1 == e) { px = v[a].X; py = v[a].Y; pz = v[a].Z; }
            else if (e2 == e) { px = v[b].X; py = v[b].Y; pz = v[b].Z; }
            else { px = mx; py = my; pz = mz; }
            return e;
        }

        void UpdateMesh(int iteration)
        {
            if (iteration > 0)
            {
                int dst = 0;
                for (int i = 0; i < triCount; i++) if (!tris[i].Deleted) tris[dst++] = tris[i];
                triCount = dst;
            }
            for (int i = 0; i < nv; i++) { v[i].Start = 0; v[i].Count = 0; }
            for (int i = 0; i < triCount; i++) { v[tris[i].V0].Count++; v[tris[i].V1].Count++; v[tris[i].V2].Count++; }
            int start = 0;
            for (int i = 0; i < nv; i++) { v[i].Start = start; start += v[i].Count; v[i].Count = 0; }
            refs.Clear();
            var arr = new Ref[triCount * 3];
            for (int i = 0; i < triCount; i++)
                for (int k = 0; k < 3; k++) { int id = Corner(ref tris[i], k); arr[v[id].Start + v[id].Count] = new Ref { Tri = i, Corner = k }; v[id].Count++; }
            refs.AddRange(arr);
            if (iteration != 0) return;

            // a vertex is on a border when one of its edges belongs to one triangle only
            var ids = new List<int>(); var counts = new List<int>();
            for (int i = 0; i < nv; i++)
            {
                ids.Clear(); counts.Clear();
                for (int j = 0; j < v[i].Count; j++)
                {
                    ref Tri tr = ref tris[refs[v[i].Start + j].Tri];
                    for (int k = 0; k < 3; k++)
                    {
                        int id = Corner(ref tr, k), ofs = ids.IndexOf(id);
                        if (ofs < 0) { ids.Add(id); counts.Add(1); } else counts[ofs]++;
                    }
                }
                for (int j = 0; j < ids.Count; j++) if (counts[j] == 1) v[ids[j]].Border = true;
            }
            // the quadric of each vertex: the planes of its triangles
            Array.Clear(q);
            for (int i = 0; i < triCount; i++)
            {
                ref Tri tr = ref tris[i];
                double ax = v[tr.V1].X - v[tr.V0].X, ay = v[tr.V1].Y - v[tr.V0].Y, az = v[tr.V1].Z - v[tr.V0].Z;
                double bx = v[tr.V2].X - v[tr.V0].X, by = v[tr.V2].Y - v[tr.V0].Y, bz = v[tr.V2].Z - v[tr.V0].Z;
                double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 0) { nx /= l; ny /= l; nz /= l; }
                tr.Nx = nx; tr.Ny = ny; tr.Nz = nz;
                double d = -(nx * v[tr.V0].X + ny * v[tr.V0].Y + nz * v[tr.V0].Z);
                for (int k = 0; k < 3; k++)
                {
                    int o = Corner(ref tr, k) * 10;
                    q[o] += nx * nx; q[o + 1] += nx * ny; q[o + 2] += nx * nz; q[o + 3] += nx * d; q[o + 4] += ny * ny; q[o + 5] += ny * nz; q[o + 6] += ny * d;
                    q[o + 7] += nz * nz; q[o + 8] += nz * d; q[o + 9] += d * d;
                }
            }
            for (int i = 0; i < triCount; i++)
            {
                ref Tri tr = ref tris[i];
                tr.E0 = EdgeError(tr.V0, tr.V1, out _, out _, out _); tr.E1 = EdgeError(tr.V1, tr.V2, out _, out _, out _); tr.E2 = EdgeError(tr.V2, tr.V0, out _, out _, out _);
                tr.E3 = Math.Min(tr.E0, Math.Min(tr.E1, tr.E2));
            }
        }

        // true when the move of vertex i0 to p turns over a triangle around it; marks the triangles that the collapse removes
        bool Flipped(double px, double py, double pz, int i0, int i1, List<bool> del)
        {
            for (int k = 0; k < v[i0].Count; k++)
            {
                var r = refs[v[i0].Start + k]; ref Tri tr = ref tris[r.Tri];
                if (tr.Deleted) continue;
                int id1 = Corner(ref tr, (r.Corner + 1) % 3), id2 = Corner(ref tr, (r.Corner + 2) % 3);
                if (id1 == i1 || id2 == i1) { del[k] = true; continue; }
                double ax = v[id1].X - px, ay = v[id1].Y - py, az = v[id1].Z - pz, la = Math.Sqrt(ax * ax + ay * ay + az * az);
                double bx = v[id2].X - px, by = v[id2].Y - py, bz = v[id2].Z - pz, lb = Math.Sqrt(bx * bx + by * by + bz * bz);
                if (la > 0) { ax /= la; ay /= la; az /= la; }
                if (lb > 0) { bx /= lb; by /= lb; bz /= lb; }
                if (Math.Abs(ax * bx + ay * by + az * bz) > 0.999) return true;
                double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 0) { nx /= l; ny /= l; nz /= l; }
                del[k] = false;
                if (nx * tr.Nx + ny * tr.Ny + nz * tr.Nz < 0.2) return true;
            }
            return false;
        }

        void UpdateTriangles(int i0, int from, List<bool> del)
        {
            int s = v[from].Start, c = v[from].Count;
            for (int k = 0; k < c; k++)
            {
                var r = refs[s + k]; ref Tri tr = ref tris[r.Tri];
                if (tr.Deleted) continue;
                if (del[k]) { tr.Deleted = true; deleted++; continue; }
                SetCorner(ref tr, r.Corner, i0);
                tr.Dirty = true;
                tr.E0 = EdgeError(tr.V0, tr.V1, out _, out _, out _); tr.E1 = EdgeError(tr.V1, tr.V2, out _, out _, out _); tr.E2 = EdgeError(tr.V2, tr.V0, out _, out _, out _);
                tr.E3 = Math.Min(tr.E0, Math.Min(tr.E1, tr.E2));
                refs.Add(r);
            }
        }

        for (int iteration = 0; iteration < 100; iteration++)
        {
            if (startCount - deleted <= targetTriangles) break;
            if (iteration % 5 == 0) UpdateMesh(iteration);
            for (int i = 0; i < triCount; i++) tris[i].Dirty = false;
            double threshold = 0.000000001; for (int k = 0; k < aggressiveness; k++) threshold *= iteration + 3;
            for (int i = 0; i < triCount; i++)
            {
                if (tris[i].E3 > threshold || tris[i].Deleted || tris[i].Dirty) continue;
                for (int j = 0; j < 3; j++)
                {
                    double e = j == 0 ? tris[i].E0 : j == 1 ? tris[i].E1 : tris[i].E2;
                    if (!(e < threshold)) continue;
                    int i0 = Corner(ref tris[i], j), i1 = Corner(ref tris[i], (j + 1) % 3);
                    // a border vertex stays where it is
                    if (v[i0].Border || v[i1].Border) continue;
                    EdgeError(i0, i1, out double px, out double py, out double pz);
                    del0.Clear(); for (int k = 0; k < v[i0].Count; k++) del0.Add(false);
                    del1.Clear(); for (int k = 0; k < v[i1].Count; k++) del1.Add(false);
                    if (Flipped(px, py, pz, i0, i1, del0)) continue;
                    if (Flipped(px, py, pz, i1, i0, del1)) continue;
                    v[i0].X = px; v[i0].Y = py; v[i0].Z = pz;
                    for (int k = 0; k < 10; k++) q[i0 * 10 + k] += q[i1 * 10 + k];
                    int start = refs.Count;
                    UpdateTriangles(i0, i0, del0);
                    UpdateTriangles(i0, i1, del1);
                    int count = refs.Count - start;
                    if (count <= v[i0].Count) { for (int k = 0; k < count; k++) refs[v[i0].Start + k] = refs[start + k]; }
                    else v[i0].Start = start;
                    v[i0].Count = count;
                    break;
                }
                if (startCount - deleted <= targetTriangles) break;
            }
        }

        // drop the removed triangles and the vertices that no triangle uses
        var map = new int[nv]; Array.Fill(map, -1);
        var outIdx = new List<int>(); var outPos = new List<double>();
        for (int i = 0; i < triCount; i++)
        {
            if (tris[i].Deleted) continue;
            for (int k = 0; k < 3; k++)
            {
                int id = Corner(ref tris[i], k);
                if (map[id] < 0) { map[id] = outPos.Count / 3; outPos.Add(v[id].X); outPos.Add(v[id].Y); outPos.Add(v[id].Z); }
                outIdx.Add(map[id]);
            }
        }
        return (outPos.ToArray(), outIdx.ToArray());
    }
}
