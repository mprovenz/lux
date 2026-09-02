using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lux.Engine.Pipeline.Registration;

/// <summary>One camera as the `CalibDataProcessor` sees it (spec `ad108613f7a1daa2b.md` §0): the stereo image
/// `CDP+0x70[c]`, its saturation level `CDP+0x80[c]`, the CURRENT stage-1 slot (written in place), the view pose `CDP+0x90[c]`
/// and the per-capture facts the state machines read from the CapturedImage.</summary>
public sealed class CdpCamera
{
    public int Id; public string Name = "";
    public int SensorType = 2;                       // FUN_180125650(img)[0]; the fine optimizer only runs on type 2
    public Rgba8Image Image;                 // CDP+0x70[c] (dense RGBA8, half-res stereo image)
    public float SatLevel = 255f;                    // CDP+0x80[c]
    public bool Gray;                                // (g0|g1) >> 31 of FUN_180125590(img): uniform (channel-0) weights
    public CalibDataFull Slot = null!;               // CURRENT stage-1 slot (K,R,t are overwritten by the optimisers / BA)
    public CalibDataFull FactorySlot = null!;        // stage-0 slot (nominal fallback in FUN_1802aea30)
    public ViewPose Pose = null!;                    // CDP+0x90[c]
    public (float X, float Y) Centre;                // FUN_1802b3c90(cdp, c): normalised matching centre
    public bool StoredResult;                        // FUN_1802b42d0: camera contributes no observations
    public MirrorSystem? Mirror; public ActuatorMapping? Map; public double Hall;
    public int FrameW = 4160, FrameH = 3120;         // FUN_180125640(img)+8/+0xc (native capture size)
    public Geometry.RatPolyMapping? Poly; public float PpX, PpY, Pix;   // CRA distortion for the aligned-calibration warp map
    public CalibDataFull View() => ViewTransform.Apply(Pose, Slot);
    public static Geometry.CameraCalib ToCamera(CalibDataFull c) => new((float[])c.K.Clone(), (float[])c.T.Clone(), (float[])c.R.Clone(), c.OffX, c.OffY, c.ScX, c.ScY);
}

/// <summary>The WIDE sparse driver `FUN_1802e80c0` (spec `a868996653d4faf7c.md` §2) assembled from the ported pieces: target pyramid,
/// alignment offsets, warp field, saturation masks, per-level matching (initLowerA/initLowerB at the top by `mode`, matchFeatures
/// below), RANSAC gate, view update, finalize.</summary>
public static class WideSparseDriver
{
    /// <summary>The image as a dense (stride = width, no pad) RGBA8 buffer.</summary>
    public static byte[] Dense(Rgba8Image img)
    {
        if (img.Stride == img.W && img.Pad == 0) return img.Data;
        var d = new byte[img.W * img.H * 4];
        for (int y = 0; y < img.H; y++) Array.Copy(img.Data, ((y + img.Pad) * img.Stride + img.Pad) * 4, d, y * img.W * 4, img.W * 4);
        return d;
    }

    public static (float X, float Y)[] Run(PaddedRgba8[] refPyr, FeaturePoint[][] feats, int nRefPts, Rgba8Image B, CalibData calibA, CalibData calibB,
        float planeDepth, bool bidir, float satLevel, bool mono, int mode, (float X, float Y) refCentre, (float X, float Y) camCentre, Action<string>? log = null)
    {
        int n = feats.Length;
        var pyrB = SparseLnrPyramid.Build(Dense(B), B.W, B.H, n, 8);
        var guess = (refCentre.X - camCentre.X, refCentre.Y - camCentre.Y);   // this+0x10 − this+0x254
        var (offs, ok) = SparseLnrPyramid.Align(refPyr, pyrB, refCentre, guess, mono ? SparseLnrPyramid.AlignWeightsMono : SparseLnrPyramid.AlignWeightsColour);
        log?.Invoke($"  align offs {string.Join(" ", offs.Select(o => $"({o.X},{o.Y})"))} ok {ok}");
        var W = new SparseLnrMatch.WarpField { M = Mat4D.FlowMatrix(calibA, calibB), Sx = 1f, Sy = 1f };
        bool farScene = planeDepth > 6000f;
        var viewA = new MatchView { Id = 1, Enabled = (mode == 0 && planeDepth < 6000f) || mode == 1 || mode == 2 };
        var viewB = new MatchView { Id = 2, Enabled = true };
        var ptsA = new SparseLnrRansac.ViewPoints(n); var ptsB = new SparseLnrRansac.ViewPoints(n);
        var perLevel = new MatchedPoint[n][]; int minInl = 8; int top = n - 1;
        for (int l = top; l >= 0; l--)
        {
            var mask = SparseLnrPyramid.SaturationMask(pyrB[l], satLevel);
            var Bl = pyrB[l].AsImage(); var A = refPyr[l].AsImage();
            // 1802e856a–1802e8709: at the top level `mode == 0` → initLowerACamera, `mode − 1 <u 2` (i.e. 1 or 2) →
            // initLowerBCamera, any other value → no initialiser at all (unreachable from CalibDataProcessor, which only
            // ever passes 0/1/2).
            MatchedPoint[] m = l == top
                ? (mode == 0 ? SparseLnrMatch.InitLowerA(A, Bl, mask, pyrB[l].W, offs[l], feats[l], l, W, viewA, viewB, bidir, mono, calibA, calibB, planeDepth)
                 : mode == 1 || mode == 2 ? SparseLnrMatch.InitLowerB(A, Bl, mask, pyrB[l].W, offs[l], feats[l], l, W, viewA, viewB, bidir, mono)
                 : throw new NotSupportedException($"SparseLNR mode {mode} has no top-level initialiser"))
                : SparseLnrMatch.MatchLevel(A, Bl, mask, pyrB[l].W, offs[l], feats[l], viewA, viewB, farScene, bidir, mono);
            if (m.Length != feats[l].Length) throw new InvalidOperationException("feature size mismatch");
            // 1802e8837–1802e8863: thrA is ALWAYS 2.0f/wB (xmm7, passed in xmm3); only thrB takes the far-scene
            // 0.6f (DAT_1806bba74). Passing thr twice is invisible while view A is disabled — which it is on every
            // A-reference capture — but wrong for mode 1/2, where view A is the working view.
            float wB = pyrB[l].W; float thrA = 2.0f / wB, thrB = (farScene ? BitConverter.Int32BitsToSingle(0x3f19999a) : 2.0f) / wB;
            if (log is not null)
            {   // one summary line per level, in the same shape as cp.dll's own per-level log lines so the two logs can be compared side by side
                int f1 = 0, s0 = 0, s3 = 0, s4a = 0, s4b = 0;
                foreach (var r in m) { if (r.Status == 1) f1++; else if (r.Status == 0) s0++; else if (r.Status == 3) s3++; else if (r.Octave == 1) s4a++; else s4b++; }
                log($"  L{l} {(l != top ? "match" : mode == 0 ? "initA" : "initB")}: {m.Length} feats → fail {f1} lowtex {s0} weak {s3} good {s4a + s4b} (A {s4a} B {s4b}), minInl {minInl}");
            }
            SparseLnrRansac.Gate(m, viewA, viewB, feats[l], thrA, thrB, minInl);
            SparseLnrRansac.UpdateView(viewA, ptsA, l, feats[l], m, l != top);
            SparseLnrRansac.UpdateView(viewB, ptsB, l, feats[l], m, l != top);
            perLevel[l] = m;
            if (!viewA.Enabled && !viewB.Enabled) { log?.Invoke("  both views disabled → abort"); return Enumerable.Repeat((-1f, -1f), nRefPts).ToArray(); }
            minInl = (int)((double)minInl * 1.5);
        }
        return SparseLnrRansac.Finalize(perLevel, nRefPts);
    }
}

/// <summary>
/// `CalibDataProcessor` (spec `ad108613f7a1daa2b.md`): the WIDE state machine `runReferenceGroupCams` (`1802af340`, states
/// 0 → 2 → 3×N → 6×N → 4×N → 7 → 8): reference features → points, per-camera WIDE sparse matching, fundamental-matrix filter,
/// fine mirror optimizer (type-2 sensors only), triangulate + refine, sparse BA (mask schedule by `CDP+0xfc`) with the nominal-centre /
/// reprojection acceptance. Poses are never written here; calibration slots are written in place.
/// </summary>
public sealed class CalibDataProcessor
{
    public CdpCamera Ref = null!;
    public List<CdpCamera> RefGroup = new();     // FUN_1802af870(cams, 0): reference-group cams excluding ref, capture (file) order
    public float Z = 1000f;                       // CDP+0xf8 (FUN_1802acc90)
    public bool WideFlag = true;                  // CDP+0xfc (FUN_1802ad350)
    public int CamsType;                          // FUN_180111c40(cams): 0 on the L16
    public (float Min, float Max) ZRange => CamsType == 1 ? (70f, 40000f) : (200f, 640000f);
    public Action<string>? Log;

    public PaddedRgba8[] RefPyr = null!; public FeaturePoint[][] RefFeats = null!; public (float X, float Y)[] RefPts = null!;
    public TriPoint[] Points = Array.Empty<TriPoint>();                                    // CDP+0x40
    public SortedDictionary<int, (float X, float Y)[]> Obs = new();                         // CDP+0x50
    public Dictionary<string, Dictionary<int, float>> Snapshots = new();                    // evaluator records (FUN_1802d84f0)
    public Dictionary<int, bool> Accepted = new();

    IEnumerable<CdpCamera> All() { yield return Ref; foreach (var c in RefGroup) yield return c; }

    /// <summary>`FUN_1802d84f0(ev, name)`: the live mean reprojection error of the ref and every observed camera.</summary>
    public void Snapshot(string name)
    {
        var rec = new Dictionary<int, float>();
        rec[Ref.Id] = SparseBaCaller.MeanReprojection(Ref.Pose, Ref.Slot, Points, Points.Select(p => (p.U, p.V)).ToArray());
        foreach (var c in RefGroup) if (Obs.TryGetValue(c.Id, out var o)) rec[c.Id] = SparseBaCaller.MeanReprojection(c.Pose, c.Slot, Points, o);
        Snapshots[name] = rec;
        Log?.Invoke($"  evaluator '{name}': {string.Join(" ", rec.Select(kv => $"cam{kv.Key} {kv.Value:G6}"))}");
    }

    /// <summary>λ1 (state 2): reference pyramid (4 levels, pad 8), detector on channel 0 with margin 6 (level 0 unculled, others maxCount 4000),
    /// points = levels 0–2 scaled by 2^level, appended as `{obs = p, X = 0}`.</summary>
    public void ReferenceFeatures()
    {
        const int levels = 4;
        RefPyr = SparseLnrPyramid.Build(WideSparseDriver.Dense(Ref.Image), Ref.Image.W, Ref.Image.H, levels, 8);
        RefFeats = new FeaturePoint[levels][];
        // detect(maxCount 4000, levels 2): `mc = (i == 0) ? maxCount : -1` (cmp r15,1; sbb eax,eax; not eax; or eax,r14d → level 0 culled to 4000, others unculled;
        // verified on L16_00405 where level 0 has 8509 raw corners → 2700, and on 00466 where 3078 < 4000 leaves it unculled)
        for (int l = 0; l < levels; l++) RefFeats[l] = FeatureDetector.FindFeatures(RefPyr[l].Dense(), RefPyr[l].W, RefPyr[l].H, l == 0 ? 4000 : -1, 6, true).ToArray();
        var pts = new List<(float X, float Y)>();
        for (int s = 0; s <= 2; s++) { float scale = (float)(int)Math.ScaleB(1.0, s); foreach (var p in RefFeats[s]) pts.Add((p.X * scale, p.Y * scale)); }
        RefPts = pts.ToArray();
        Points = RefPts.Select(p => new TriPoint { U = p.X, V = p.Y }).ToArray();
        Log?.Invoke($"  reference features {string.Join("/", RefFeats.Select(f => f.Length))} → {RefPts.Length} points");
    }

    /// <summary>λ2 (state 3) for one camera: the WIDE sparse driver on `Apply(pose[ref], calib(ref))` / `Apply(pose[c], calib(c))`.</summary>
    public (float X, float Y)[] SparseWide(CdpCamera c)
    {
        if (c.StoredResult) { var none = Enumerable.Repeat((-1f, -1f), Points.Length).ToArray(); Obs[c.Id] = none; return none; }
        // λ1 step 1 (`1802be477–1802be48d`): the mode is a function of the reference module's *identity*, not its group —
        // `mode = (ref == 8) ? 1 : (ref == 14) ? 2 : 0`, i.e. B4 → 1, C5 → 2, everything else (including C1–C4/C6) → 0.
        int mode = Ref.Id == 8 ? 1 : Ref.Id == 14 ? 2 : 0;
        var refView = Ref.View().Basic(); var camView = c.View().Basic();
        var res = WideSparseDriver.Run(RefPyr, RefFeats, RefPts.Length, c.Image, refView, camView, Z, true, c.SatLevel, c.Gray, mode, Ref.Centre, c.Centre, Log);
        Obs[c.Id] = res;
        return res;
    }

    /// <summary>λ3 (state 6): `FundamentalMatrixFilter::filter` per camera in list order (reset at the first camera).</summary>
    public void FilterAll()
    {
        var refFlat = new float[RefPts.Length * 2]; for (int i = 0; i < RefPts.Length; i++) { refFlat[2 * i] = RefPts[i].X; refFlat[2 * i + 1] = RefPts[i].Y; }
        foreach (var c in RefGroup)
        {
            var o = Obs[c.Id]; var flat = new float[o.Length * 2]; for (int i = 0; i < o.Length; i++) { flat[2 * i] = o[i].X; flat[2 * i + 1] = o[i].Y; }
            FundamentalMatrixFilter.Filter(refFlat, flat);
            for (int i = 0; i < o.Length; i++) o[i] = (flat[2 * i], flat[2 * i + 1]);
        }
    }

    /// <summary>λ4 (state 4): the fine mirror optimizer `(fp 0, cf 0, seedθ −1, seedC 0)` for type-2 sensors; writes the accepted pose into the slot.</summary>
    public void FineAll()
    {
        var refCam = Ref.View().Basic();
        foreach (var c in RefGroup)
        {
            if (c.SensorType != 2) { Log?.Invoke($"  fine optimizer: cam {c.Id} sensor type {c.SensorType} → skipped"); continue; }
            var o = Obs[c.Id]; var flat = new float[o.Length * 2]; for (int i = 0; i < o.Length; i++) { flat[2 * i] = o[i].X; flat[2 * i + 1] = o[i].Y; }
            var r = SparseMirrorAngleOptimizer.Optimize(c.Mirror!, c.Slot, c.Pose, refCam, flat, Points, 0, 0, -1.0, (0f, 0f), Z, WideFlag, c.Map, c.Hall);
            Log?.Invoke($"  fine optimizer cam {c.Id}: accepted {r.Accepted} θ {r.Theta:R}");
            if (r.Accepted && r.Written != null) { c.Slot.K = (float[])r.Written.K.Clone(); c.Slot.R = (float[])r.Written.R.Clone(); c.Slot.T = (float[])r.Written.T.Clone(); }
        }
    }

    /// <summary>λ5 (state 7): `Triangulator::triangulate` then `refine3dPoints`, evaluator snapshots "1. init" / "2. point BA" in between.</summary>
    public void TriangulateAndRefine()
    {
        var refCam = Ref.View().Basic();
        var ids = Obs.Keys.ToArray();
        var cams = ids.Select(id => RefGroup.First(c => c.Id == id).View().Basic()).ToArray();
        var obs = ids.Select(id => { var o = Obs[id]; var f = new float[o.Length * 2]; for (int i = 0; i < o.Length; i++) { f[2 * i] = o[i].X; f[2 * i + 1] = o[i].Y; } return f; }).ToArray();
        var (P, _, near, far) = Triangulator.Triangulate(Points, refCam, cams, obs);
        Points = P;
        Snapshot("1. init");
        Points = DepthRefine.Refine(Points, refCam, cams, obs, ZRange.Min, ZRange.Max);
        Snapshot("2. point BA");
    }

    /// <summary>`FUN_1802aea30`: expected camera centres by module name (table by `FUN_180111c40`), else the factory (stage-0) centre.</summary>
    public static readonly Dictionary<string, (double X, double Y, double Z)> NominalTable0 = new()
    {
        ["A1"] = (0, 0, 0), ["A2"] = (-27.69, -23.74, 0), ["A3"] = (8.53, -23.26, 0), ["A4"] = (24.28, 23.61, 0), ["A5"] = (-43.39, 1.12, 0),
        ["B4"] = (-7.64, 12.64, -10.558), ["C5"] = (37.2, -5.64, -12.467), ["C6"] = (34.77, 34.91, -12.567),
    };
    public static readonly Dictionary<string, (double X, double Y, double Z)> NominalTable1 = new()
    {
        ["A1"] = (0, 0, 0), ["A2"] = (-6.45, 11.17, 0), ["A3"] = (12.9, 0, 0), ["A4"] = (-6.45, -11.17, 0), ["A5"] = (-12.9, 0, 0),
    };
    public float[] Nominal(CdpCamera c)
    {
        var tbl = CamsType == 0 ? NominalTable0 : NominalTable1;
        if (tbl.TryGetValue(c.Name, out var v)) return new[] { (float)v.X, (float)v.Y, (float)v.Z };
        return SparseBaCaller.Centre(c.FactorySlot.R, c.FactorySlot.T);
    }

    /// <summary>`FUN_1802dc770` wrapper: the BA caller over the current slots, written slots applied in place.</summary>
    public SparseBaCaller.Result RunBa(uint mask, bool b8, bool b9, bool isHigher, IEnumerable<CdpCamera> members, HashSet<int> exclusion)
    {
        var cams = new Dictionary<int, SparseBaCaller.CamInput>();
        foreach (var c in members) cams[c.Id] = new() { Cam = c.Id, Pose = c.Pose, Slot = c.Slot };
        var res = SparseBaCaller.Run(Points, Obs, Ref.Id, cams, isHigher, mask, exclusion, b8, b9, Log);
        foreach (var kv in res.Written) { var c = members.First(x => x.Id == kv.Key); c.Slot.K = (float[])kv.Value.K.Clone(); c.Slot.R = (float[])kv.Value.R.Clone(); c.Slot.T = (float[])kv.Value.T.Clone(); }
        return res;
    }

    static float Dist(float[] a, float[] b)
    {
        float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        float d2 = (dz * dz + dx * dx) + dy * dy;
        if (d2 == 0f) return 0f;
        float r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(d2)).ToScalar(), s = d2 * r;
        return (((s * r) + (-3.0f)) * s) * (-0.5f);
    }

    /// <summary>λ6 (state 8): the WIDE BA schedule (`2 − (type==1)` passes; `CDP+0xfc` selects masks 8/1/(0x10,0x20) or (0x30)/9) and the acceptance
    /// against the nominal centres (≤ 8) and the live reprojection error (≤ 4 when ref &lt; 5 else 15); rejected cams restore the pre-BA slot.</summary>
    public void BundleAdjustWide()
    {
        var exclusion = new HashSet<int>();
        if (RefGroup.Count == 0) { Snapshot("3. Camera BA"); return; }
        var saved = All().ToDictionary(c => c.Id, c => c.Slot.Clone());
        int passes = 2 - (CamsType == 1 ? 1 : 0);
        for (int pass = 0; pass < passes; pass++)
        {
            if (!WideFlag)
            {
                RunBa(8, false, false, false, All(), exclusion); RunBa(1, false, false, false, All(), exclusion);
                if (Ref.Id < 5) { RunBa(0x10, false, false, false, All(), exclusion); RunBa(0x20, false, false, false, All(), exclusion); }
            }
            else
            {
                if (CamsType == 1) RunBa(0x30, true, true, false, All(), exclusion);
                RunBa(9, CamsType == 1, CamsType == 1, false, All(), exclusion);
            }
        }
        float thr = Ref.Id < 5 ? 4.0f : 15.0f;
        foreach (var c in RefGroup)
        {
            var t = SparseBaCaller.Centre(c.Slot.R, c.Slot.T); var nom = Nominal(c);
            float d = Dist(t, nom);
            float err = Obs.TryGetValue(c.Id, out var o) ? SparseBaCaller.MeanReprojection(c.Pose, c.Slot, Points, o) : 0f;
            bool keep = d <= 8.0f && !exclusion.Contains(c.Id) && !(thr < err);
            Accepted[c.Id] = keep;
            Log?.Invoke($"  acceptance cam {c.Id} ({c.Name}): centre ({t[0]:G6},{t[1]:G6},{t[2]:G6}) nominal ({nom[0]:G6},{nom[1]:G6},{nom[2]:G6}) d {d:G6} reproj {err:G6} → {(keep ? "keep" : "RESTORE")}");
            if (!keep) { var s = saved[c.Id]; c.Slot.K = (float[])s.K.Clone(); c.Slot.R = (float[])s.R.Clone(); c.Slot.T = (float[])s.T.Clone(); }
        }
        Snapshot("3. Camera BA");
    }

    /// <summary>`runReferenceGroupCams` (states 0 → 2 → 3×N → 6×N → 4×N → 7 → 8).</summary>
    public void RunReferenceGroupCams()
    {
        if (RefGroup.Count == 0) throw new InvalidOperationException("no lower src cams are enabled. cannot compute depth");
        ReferenceFeatures();
        foreach (var c in RefGroup) SparseWide(c);
        FilterAll();
        FineAll();
        TriangulateAndRefine();
        BundleAdjustWide();
    }

    // ───────────────────────────── TELE (higher group) ─────────────────────────────
    public List<CdpCamera> Higher = new();        // FUN_1802b10e0(cams): higher-group cams, capture (file) order
    public float[] Depth = Array.Empty<float>(); public int DepthW, DepthH;   // CDP+0x60: dense depth in the reference image frame
    public MirrorAngleOptimizerCoarse.Context? Coarse;                        // CDP+0x18
    public Dictionary<int, double> ThetaMap = new(); public Dictionary<int, (float X, float Y)> CMap = new();
    public Dictionary<int, (float Err, CalibDataFull Slot)> Saved = new(); public HashSet<int> Excl = new();
    public Dictionary<int, CalibData> FineWritten = new();     // the fine optimizer's accepted write per camera (before any restore)
    public Dictionary<int, (int W, int H)> CanvasSize = new();               // api+0x3b8[c] = 2·size

    /// <summary>API state 6 for one higher-group camera (prior spec §1.5): `scale2 = shift2 = 0.5`, `GetAlignedCalibration(refView, camView, img, (w/2,h/2), (1,1))`,
    /// then the canvas pose `scale3 = K_al/K_cam`, `shift3 = K_cam[2] − K_al[2]/scale3`, `Q = R_al·R_camᵀ`. Returns the aligned calib and size (the CreateStereoImage inputs).</summary>
    public (Geometry.CameraCalib Aligned, Geometry.CameraCalib Module, (int W, int H) Size) CanvasPose(CdpCamera c)
    {
        var refView = CdpCamera.ToCamera(Ref.View());
        c.Pose.Scale2 = (0.5f, 0.5f); c.Pose.Shift2 = (0.5f, 0.5f);
        var camView = CdpCamera.ToCamera(c.View());
        var r = Geometry.AlignedCalibrationScan.Compute(refView, camView, (c.FrameW / 2, c.FrameH / 2), (1f, 1f), 1f, 1f, c.PpX, c.PpY, c.Poly!, c.Pix, c.Pix);
        var al = r.First;
        float s3x = al.K[0] / camView.K[0], s3y = al.K[4] / camView.K[4];
        c.Pose.Scale3 = (s3x, s3y);
        c.Pose.Shift3 = (camView.K[2] - al.K[2] / s3x, camView.K[5] - al.K[5] / s3y);
        c.Pose.Q = Geometry.Mat3F.MulABt(al.R, camView.R);
        CanvasSize[c.Id] = (2 * r.W, 2 * r.H);
        Log?.Invoke($"  canvas cam {c.Id} ({c.Name}): min ({r.MinX},{r.MinY}) size {r.W}x{r.H} scale3 ({s3x:R},{s3y:R}) shift3 ({c.Pose.Shift3.X:R},{c.Pose.Shift3.Y:R})");
        return (al, camView, (r.W, r.H));
    }

    /// <summary>API state 7 init (`FUN_1802b32e0` + `FUN_180291c40`): the coarse optimizer context from the reference image, the depth image and `Apply(pose[ref], calib(ref))`.</summary>
    public void InitCoarse()
    {
        if (DepthW != Ref.Image.W || DepthH != Ref.Image.H) throw new InvalidOperationException("Size of image and reference do not match.");
        var refView = Ref.View();
        var ref8 = ViewTransform.Scale(refView, 0.125f, 0.125f);   // FUN_180308670: refCam × 0.125
        Coarse = MirrorAngleOptimizerCoarse.Build(Ref.Image, Depth, DepthW, DepthH, ref8, Z);
    }

    /// <summary>λ8 (state 1): the coarse `MirrorAngleOptimizer` per type-2 camera; θ/c seeds kept for the fine pass; the slot is written by the optimizer.</summary>
    public void CoarseAll()
    {
        foreach (var c in Higher)
        {
            if (c.SensorType != 2) { Log?.Invoke($"  coarse: cam {c.Id} sensor type {c.SensorType} → skipped"); continue; }
            double theta0 = c.Map!.Angle(c.Hall);
            var r = MirrorAngleOptimizerCoarse.Optimize(Coarse!, c.Mirror!, c.Slot, c.Pose, c.Image, theta0);
            ThetaMap[c.Id] = r.Theta; CMap[c.Id] = (r.Cx, r.Cy);
            c.Slot.K = (float[])r.Written.K.Clone(); c.Slot.R = (float[])r.Written.R.Clone(); c.Slot.T = (float[])r.Written.T.Clone();
            Log?.Invoke($"  coarse cam {c.Id} ({c.Name}): θ0 {theta0:R} → θ {r.Theta:R} c ({r.Cx:R},{r.Cy:R})");
        }
    }

    /// <summary>λ9 (state 3): prior points (`FUN_1802b4c40`) + the TELE sparse driver (`FUN_1802ea1c0`) on `Apply(pose[ref], calib(ref))` / `Apply(pose[c], calib(c))`.
    /// `planeDepth`/`farScene` are the values the last WIDE driver call left on the shared SparseLNR.</summary>
    public (float X, float Y)[] SparseTele(CdpCamera c)
    {
        if (c.StoredResult) { var none = Enumerable.Repeat((-1f, -1f), Points.Length).ToArray(); Obs[c.Id] = none; return none; }
        var refView = Ref.View().Basic(); var camView = c.View().Basic();
        var M = Mat4D.FlowMatrix(refView, camView);
        var prior = TeleSparseDriver.PriorPoints(Points, Depth, DepthW, DepthH, M, c.Image.W, c.Image.H);
        var res = TeleSparseDriver.Run(RefPyr, RefFeats, RefPts.Length, WideSparseDriver.Dense(c.Image), c.Image.W, c.Image.H, prior, Depth, DepthW, DepthH, M, 1f, 1f, Z, Z > 6000f, c.SatLevel, Log).Out;
        Obs[c.Id] = res;
        return res;
    }
    public (float X, float Y)[] PriorPoints(CdpCamera c)
    {
        var M = Mat4D.FlowMatrix(Ref.View().Basic(), c.View().Basic());
        return TeleSparseDriver.PriorPoints(Points, Depth, DepthW, DepthH, M, c.Image.W, c.Image.H);
    }

    /// <summary>λ10 (state 6): FMF per camera, then the "1. MirrorOpt" snapshot and the per-camera baseline `{err, slot}`.</summary>
    public void FilterTele()
    {
        var refFlat = new float[RefPts.Length * 2]; for (int i = 0; i < RefPts.Length; i++) { refFlat[2 * i] = RefPts[i].X; refFlat[2 * i + 1] = RefPts[i].Y; }
        foreach (var c in Higher)
        {
            var o = Obs[c.Id]; var flat = new float[o.Length * 2]; for (int i = 0; i < o.Length; i++) { flat[2 * i] = o[i].X; flat[2 * i + 1] = o[i].Y; }
            FundamentalMatrixFilter.Filter(refFlat, flat);
            for (int i = 0; i < o.Length; i++) o[i] = (flat[2 * i], flat[2 * i + 1]);
        }
        SnapshotTele("1. MirrorOpt");
        foreach (var c in Higher) Saved[c.Id] = (Snapshots["1. MirrorOpt"].GetValueOrDefault(c.Id, 0f), c.Slot.Clone());
    }

    /// <summary>`FUN_1802d84f0` on the TELE evaluator: the ref and every camera key of `CDP+0x50`.</summary>
    public void SnapshotTele(string name)
    {
        var rec = new Dictionary<int, float>();
        rec[Ref.Id] = SparseBaCaller.MeanReprojection(Ref.Pose, Ref.Slot, Points, Points.Select(p => (p.U, p.V)).ToArray());
        foreach (var kv in Obs) { var c = AllCams().First(x => x.Id == kv.Key); rec[c.Id] = SparseBaCaller.MeanReprojection(c.Pose, c.Slot, Points, kv.Value); }
        Snapshots[name] = rec;
        Log?.Invoke($"  evaluator '{name}': {string.Join(" ", rec.Select(kv => $"cam{kv.Key} {kv.Value:G6}"))}");
    }
    IEnumerable<CdpCamera> AllCams() { yield return Ref; foreach (var c in RefGroup) yield return c; foreach (var c in Higher) yield return c; }

    /// <summary>λ11 (state 5): the fine optimizer `(fp 2, cf 1, θ/c seeds)` per type-2 camera, then "2. ReprojOpt": accept when `new ≤ baseline`, else restore and exclude.</summary>
    public void FineTele()
    {
        var refCam = Ref.View().Basic();
        foreach (var c in Higher)
        {
            if (c.SensorType != 2) { Log?.Invoke($"  fine: cam {c.Id} sensor type {c.SensorType} → skipped"); continue; }
            var seedC = CMap.GetValueOrDefault(c.Id, (0f, 0f)); double seedT = ThetaMap.GetValueOrDefault(c.Id, 0.0);
            var o = Obs[c.Id]; var flat = new float[o.Length * 2]; for (int i = 0; i < o.Length; i++) { flat[2 * i] = o[i].X; flat[2 * i + 1] = o[i].Y; }
            var r = SparseMirrorAngleOptimizer.Optimize(c.Mirror!, c.Slot, c.Pose, refCam, flat, Points, 2, 1, seedT, seedC, Z, WideFlag, c.Map, c.Hall);
            Log?.Invoke($"  fine cam {c.Id} ({c.Name}): accepted {r.Accepted} θ {r.Theta:R} δ {r.Delta:R} c ({r.Cx:R},{r.Cy:R})");
            if (r.Accepted && r.Written != null) { FineWritten[c.Id] = r.Written; c.Slot.K = (float[])r.Written.K.Clone(); c.Slot.R = (float[])r.Written.R.Clone(); c.Slot.T = (float[])r.Written.T.Clone(); }
        }
        SnapshotTele("2. ReprojOpt");
        foreach (var c in Higher)
        {
            float nw = Snapshots["2. ReprojOpt"].GetValueOrDefault(c.Id, 0f); (float Err, CalibDataFull Slot) s = Saved.TryGetValue(c.Id, out var sv) ? sv : (0f, c.Slot.Clone());
            if (nw <= s.Err) { Saved[c.Id] = (nw, c.Slot.Clone()); Log?.Invoke($"  reproj cam {c.Id}: {nw:G6} ≤ {s.Err:G6} → accept"); }
            else { c.Slot.K = (float[])s.Slot.K.Clone(); c.Slot.R = (float[])s.Slot.R.Clone(); c.Slot.T = (float[])s.Slot.T.Clone(); Excl.Add(c.Id); Log?.Invoke($"  reproj cam {c.Id}: {nw:G6} > {s.Err:G6} → RESTORE + exclude"); }
        }
    }

    /// <summary>λ12 (state 8): BA twice (mask 0xb, higher group, exclusions), "3. Camera BA", restore the state-5 baseline when `min(thr, baseline) &lt; new`.</summary>
    public void BundleAdjustTele()
    {
        RunBa(0xb, false, false, true, AllCams(), Excl);
        RunBa(0xb, false, false, true, AllCams(), Excl);
        SnapshotTele("3. Camera BA");
        float thr = Ref.Id < 5 ? 4.0f : 15.0f;
        foreach (var c in Higher)
        {
            (float Err, CalibDataFull Slot) s = Saved.TryGetValue(c.Id, out var sv) ? sv : (0f, c.Slot.Clone()); float nw = Snapshots["3. Camera BA"].GetValueOrDefault(c.Id, 0f);
            if (Excl.Contains(c.Id)) { Log?.Invoke($"  BA acceptance cam {c.Id}: excluded"); continue; }
            float lim = thr <= s.Err ? thr : s.Err; bool restore = lim < nw;
            Accepted[c.Id] = !restore;
            Log?.Invoke($"  BA acceptance cam {c.Id} ({c.Name}): new {nw:G6} lim {lim:G6} → {(restore ? "RESTORE" : "keep")}");
            if (restore) { c.Slot.K = (float[])s.Slot.K.Clone(); c.Slot.R = (float[])s.Slot.R.Clone(); c.Slot.T = (float[])s.Slot.T.Clone(); }
        }
    }

    /// <summary>`runHigherGroupCams` (states 0 → 1×N → 3×N → 6×N → 5×N → 8). Returns false when there is no higher-group camera.</summary>
    public bool RunHigherGroupCams()
    {
        if (Higher.Count == 0) return false;
        CoarseAll();
        foreach (var c in Higher) SparseTele(c);
        FilterTele();
        FineTele();
        BundleAdjustTele();
        return true;
    }
}
