namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// The RANSAC gate of `lt::SparseLNR` (`FUN_1802e5390`), `markInliers` (`FUN_1802e4640` + lambda_0 1802ec530: 4-point
/// homography RANSAC over the good matches of one view with `std::mt19937(13)` shuffled pools), the per-view homography
/// update (`FUN_180300450` / `FUN_180300790`) and the finalize step (`FUN_1802e9c50`).
/// </summary>
public static class SparseLnrRansac
{
    /// <summary>Accumulated (feature-normalised, match-normalised) pairs per level for a view (`View+0x30/+0x48`).</summary>
    public sealed class ViewPoints
    {
        public List<(float X, float Y)>[] Src, Dst;
        public ViewPoints(int nLevels) { Src = new List<(float, float)>[nLevels]; Dst = new List<(float, float)>[nLevels]; for (int i = 0; i < nLevels; i++) { Src[i] = new(); Dst[i] = new(); } }
    }

    /// <summary>`FUN_1802e5390(this, feats, matches, thrA, thrB, minInl, allowDisable)`.</summary>
    public static void Gate(MatchedPoint[] m, MatchView viewA, MatchView viewB, FeaturePoint[] feats, float thrA, float thrB, int minInl, bool allowDisable = true)
    {
        int nA = 0, nB = 0;
        foreach (var r in m) { if (r.Status == 4 && r.Octave == 1) nA++; if (r.Status == 4 && r.Octave == 2) nB++; }
        if (nB <= 5 && allowDisable) viewB.Enabled = false;
        if (nA <= 5 && allowDisable) viewA.Enabled = false;
        int Iter(int n) { long it = n >= 4 ? (long)n * (n - 1) * (n - 2) * (n - 3) / 24 : 0; if (it >= 5001 || n > 20) it = 5000; return (int)it; }
        int iterA = Iter(nA), iterB = Iter(nB);
        if (nA + nB <= 3)
        {
            viewA.Enabled = false; viewB.Enabled = true;   // 1802e55fd–: A disabled, B enabled (not both disabled)
            for (int i = 0; i < m.Length; i++) if (m[i].Status >= 3) { m[i].Status = 5; m[i].Octave = 2; }
            return;
        }
        if (nA <= 5 && nB <= 5)
        {
            viewA.Enabled = false; viewB.Enabled = true;
            for (int i = 0; i < m.Length; i++) if (m[i].Status == 4) { m[i].Status = 5; m[i].Octave = 2; }
            return;
        }
        if (nA >= 6) MarkInliers(feats, m, thrA, minInl, iterA, 1);
        if (nB >= 6) MarkInliers(feats, m, thrB, minInl, iterB, 2);
    }

    /// <summary>`markInliers(feats, matches, thr, minInl, iters, octave)`.</summary>
    public static void MarkInliers(FeaturePoint[] feats, MatchedPoint[] m, float thr, int minInl, int iters, int octave)
    {
        var cand = new List<int>();
        for (int i = 0; i < m.Length; i++) if (m[i].Octave == octave && m[i].Status == 4) cand.Add(i);
        int n = cand.Count;
        if (n < 8) { foreach (int i in cand) m[i].Status = 5; return; }
        var rng = new Mt19937(13);
        var pool = new List<int>();
        do
        {
            var t = cand.ToArray();
            for (int i = 1; i < n; i++) { int j = rng.UniformInt(i + 1); (t[i], t[j]) = (t[j], t[i]); }   // MSVC std::shuffle
            for (int i = 0; i < n - (n & 3); i++) pool.Add(t[i]);
        } while (pool.Count < 4 * iters);
        var bits = new bool[n];
        var src4 = new (float X, float Y)[4]; var dst4 = new (float X, float Y)[4];
        var local = new bool[n];
        for (int it = 0; it < iters; it++)
        {
            for (int k = 0; k < 4; k++) { int idx = pool[4 * it + k]; var f = feats[m[idx].RefIdx]; src4[k] = (f.Nx, f.Ny); dst4[k] = (m[idx].NmX, m[idx].NmY); }
            var H = Homography.FromFourPairs(src4, dst4);
            int cnt = 0;
            for (int c = 0; c < n; c++)
            {
                var f = feats[m[cand[c]].RefIdx];
                var q = SparseLnrMatch.Project(H, f.Nx, f.Ny);
                float dx = m[cand[c]].NmX - q.X, dy = m[cand[c]].NmY - q.Y, sum = dy * dy + dx * dx;
                float d = Homography.Sqrt(sum);
                local[c] = d < thr; if (local[c]) cnt++;
            }
            if (cnt >= minInl) for (int c = 0; c < n; c++) bits[c] |= local[c];
        }
        int inl = 0;
        for (int c = 0; c < n; c++) if (bits[c]) { m[cand[c]].Status = 5; inl++; }
        if (inl < 5) foreach (int i in cand) if (m[i].Status == 4) m[i].Status = 5;
    }

    /// <summary>`FUN_180300450(view, level, feats, matches, refine)`: collect the RANSAC inliers of this view, re-estimate H from
    /// the pairs of this level and the next coarser one, reject large changes when refining, disable the view if the homography is not valid.</summary>
    public static void UpdateView(MatchView view, ViewPoints pts, int level, FeaturePoint[] feats, MatchedPoint[] m, bool refine)
    {
        if (!view.Enabled || level >= pts.Src.Length) return;
        for (int i = 0; i < m.Length; i++)
            if (m[i].Octave == view.Id && m[i].Status == 5) { var f = feats[m[i].RefIdx]; pts.Src[level].Add((f.Nx, f.Ny)); pts.Dst[level].Add((m[i].NmX, m[i].NmY)); }
        var src = new List<(float X, float Y)>(); var dst = new List<(float X, float Y)>();
        // verified against the live view updates: the estimate uses this level's pairs followed by the next coarser level's
        for (int l = level; l < Math.Min(level + 2, pts.Src.Length); l++) { src.AddRange(pts.Src[l]); dst.AddRange(pts.Dst[l]); }
        float[] Hn;
        if (src.Count == 4) Hn = Homography.FromFourPairs(src.ToArray(), dst.ToArray());
        else if (src.Count >= 5) Hn = Homography.LeastSquares(src.ToArray(), dst.ToArray());
        else return;
        if (refine && Homography.Change(Hn, view.H) >= 0.6f) return;
        view.H = Hn;
        if (!Homography.IsValid(view.H)) view.Enabled = false;
    }

    /// <summary>`FUN_1802e9c50`: full-resolution matched positions for every reference point of levels 0..2 (RANSAC inliers only).</summary>
    public static (float X, float Y)[] Finalize(MatchedPoint[][] perLevel, int nRefPts)
    {
        var outp = new (float X, float Y)[nRefPts];
        for (int i = 0; i < nRefPts; i++) outp[i] = (-1f, -1f);
        int g = 0;
        for (int level = 0; level <= 2 && level < perLevel.Length; level++)
        {
            float scale = (float)(1 << level);
            foreach (var r in perLevel[level])
            {
                if (g < nRefPts && r.Status == 5) outp[g] = (r.Mx * scale, r.My * scale);
                g++;
            }
        }
        return outp;
    }
}
