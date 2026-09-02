using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Pipeline.ResAmp;

/// <summary>cp.dll `lt::Vec2&lt;int&gt;` (8 B).</summary>
public struct Vec2I
{
    public int X, Y;
    public Vec2I(int x, int y) { X = x; Y = y; }
    /// <summary>The "invalid projected grid point" marker of `ImageResolutionAmp::lambda_0` §4.1 (both lanes).</summary>
    public const int Invalid = unchecked((int)0x80000000);
    public bool IsInvalid => X == Invalid && Y == Invalid;
    public override string ToString() => $"({X},{Y})";
}

/// <summary>
/// Operations on `lt::WarpField` (<see cref="WarpField"/>, 0x50 B, spec `a-resamp.md` §1.4): column-major float `M[16]`
/// (`M[4c+r] = M(r,c)`) mapping a wide-reference full-resolution point `(x·d, y·d, d, 1)` to the tele-cache pixel (module view at canvas
/// scale σ), the depth image (`+0x40`, the `UpsampleLayer` 4160×3120 metric depth) and the grid scales `sx`/`sy` (`+0x48/+0x4c`, both 1.0f on
/// the L16 after `initResAmp` step 8).
/// </summary>
public static class WarpFieldOps
{
    /// <summary>`FUN_180301460(out, srcCalib, dstCalib, depth)`: identity + (1,1), `M = FUN_1803010a0(src, dst)` (`Mat4D.FlowMatrix`: `P_dst·inv(P_src)`
    /// in double, rounded to float column-major), then the depth pointer.</summary>
    public static WarpField FromCalibs(CalibData src, CalibData dst, float[] depth, int depthW, int depthH, int depthStride)
        => new() { M = Mat4D.FlowMatrix(src, dst), Depth = depth, DepthW = depthW, DepthH = depthH, DepthStride = depthStride, Sx = 1.0f, Sy = 1.0f };

    /// <summary>`FUN_180301ac0(wf, out)`: `(1.0f / sx, 1.0f / sy)` (divss).</summary>
    public static (float X, float Y) InverseScale(WarpField wf) => (1.0f / wf.Sx, 1.0f / wf.Sy);

    /// <summary>`FUN_180301af0(wf, s)`: `sx = 1.0f / s.x; sy = 1.0f / s.y`.</summary>
    public static void SetScale(WarpField wf, (float X, float Y) s) { wf.Sx = 1.0f / s.X; wf.Sy = 1.0f / s.Y; }

    public static WarpField Clone(WarpField wf) => new() { M = (float[])wf.M.Clone(), Depth = wf.Depth, DepthW = wf.DepthW, DepthH = wf.DepthH, DepthStride = wf.DepthStride, Sx = wf.Sx, Sy = wf.Sy };

    /// <summary>The 0x50-byte struct as dumped from cp.dll (`init_wf&lt;id&gt;_M/_builder`, `t&lt;k&gt;_warps`): M @0, depth pointer @0x40 (ignored), sx @0x48, sy @0x4c.</summary>
    public static WarpField FromBytes(ReadOnlySpan<byte> b, float[]? depth = null, int depthW = 0, int depthH = 0)
    {
        var wf = new WarpField { Depth = depth ?? Array.Empty<float>(), DepthW = depthW, DepthH = depthH, DepthStride = depthW };
        for (int i = 0; i < 16; i++) wf.M[i] = BitConverter.ToSingle(b.Slice(4 * i, 4));
        wf.Sx = BitConverter.ToSingle(b.Slice(0x48, 4)); wf.Sy = BitConverter.ToSingle(b.Slice(0x4c, 4));
        return wf;
    }
}

/// <summary>
/// The tele (non-reference lens group) WarpField chain of `PipelineCache::initResAmp` (spec §1 step 8, §1.4, §7.2, §7.3):
/// `FUN_1804f1b50` → module CalibData `FUN_1804f6640` (C3), `FUN_1803081a0` (C4 = C3 scaled by σ), reference CalibData `FUN_1804f6330`,
/// `FUN_180301460` (M), `FUN_180301af0` (sx = 1/σx), then the caller's `1/(1/sx)/σ` rescale.
/// </summary>
public static class TeleWarpFieldBuilder
{
    /// <summary>`FUN_1804f6640(api, out, camId, flag = 1)`: `Apply(pose[camId], slot(stage 1))` with the pose used in place (its scale2/shift2 = 0.5 from
    /// API state 6 still applied), then `FUN_1803081a0(·, (2,2))` and `FUN_1803086a0(·, −0.5, −0.5)` = C3.</summary>
    public static CalibDataFull ModuleCalib(ViewPose pose, CalibDataFull stage1Slot)
        => ViewTransform.Shift(ViewTransform.Scale(ViewTransform.Apply(pose, stage1Slot), 2.0f, 2.0f), -0.5f, -0.5f);

    /// <summary>`FUN_1804f6330(api, out, refId, flag = 1)`: a copy of `pose[ref]` with `scale2 = (1,1)` (`FUN_1802e4340`) and `shift2 = (0,0)` (`FUN_1802e4350`),
    /// applied to the reference stage-1 slot.</summary>
    public static CalibDataFull ReferenceCalib(ViewPose refPose, CalibDataFull refStage1Slot)
    {
        var p = new ViewPose
        {
            P = (float[])refPose.P.Clone(), U = (float[])refPose.U.Clone(), Q = (float[])refPose.Q.Clone(),
            Scale1 = refPose.Scale1, Shift1 = refPose.Shift1, Scale2 = (1.0f, 1.0f), Shift2 = (0.0f, 0.0f), Shift3 = refPose.Shift3, Scale3 = refPose.Scale3,
        };
        return ViewTransform.Apply(p, refStage1Slot);
    }

    /// <summary>`FUN_1804f1b50` after the CalibData lookups: `modS = FUN_1803081a0(C3, scale)`, `wf = FUN_180301460(ref, modS, depth)`,
    /// `FUN_180301af0(wf, scale)` (sx = 1/σx, sy = 1/σy) = the `init_wf&lt;id&gt;_builder` state.</summary>
    public static WarpField Build(CalibDataFull moduleC3, CalibDataFull reference, (float X, float Y) scale, float[] depth, int depthW, int depthH)
    {
        var modS = ViewTransform.Scale(moduleC3, scale.X, scale.Y);
        var wf = WarpFieldOps.FromCalibs(reference.Basic(), modS.Basic(), depth, depthW, depthH, depthW);
        WarpFieldOps.SetScale(wf, scale);
        return wf;
    }

    /// <summary>`initResAmp` step 8 after the builder: `a = FUN_180301ac0(wf)`, `c = (a.x / σx, a.y / σy)`, `FUN_180301af0(wf, c)` → on the L16 exactly (1.0f, 1.0f).</summary>
    public static WarpField Rescale(WarpField wf, (float X, float Y) scale)
    {
        var a = WarpFieldOps.InverseScale(wf);
        WarpFieldOps.SetScale(wf, (a.X / scale.X, a.Y / scale.Y));
        return wf;
    }

    /// <summary>The whole chain from the registration state (`api+0x3c8` pose map + stage-1 slots) for one tele camera.</summary>
    public static WarpField BuildFromPoses(ViewPose modulePose, CalibDataFull moduleSlot, ViewPose refPose, CalibDataFull refSlot, (float X, float Y) scale, float[] depth, int depthW, int depthH)
        => Rescale(Build(ModuleCalib(modulePose, moduleSlot), ReferenceCalib(refPose, refSlot), scale, depth, depthW, depthH), scale);
}

/// <summary>
/// `ImageResolutionAmp::lambda_0` §2.3 tile geometry (canvas tile → 8-aligned wide-reference region + the 8-px coarse grid) and §4.1
/// (the WarpField projection of that grid into a module's tele-cache pixels), transcribed from the disassembly (18043ac3a–18043af57,
/// 18043b5d0–18043b775).
/// </summary>
public static class WarpFieldGrid
{
    /// <summary>The wide-reference region of a canvas tile: `x0c..x1c` (inclusive-exclusive, `w = 8m+1`) and the grid `(x0c + 8i, y0c + 8j)`.</summary>
    public sealed record TileGeometry(int X0c, int Y0c, int X1c, int Y1c, Image<Vec2I> Grid)
    {
        public int W => X1c - X0c;
        public int H => Y1c - Y0c;
    }

    /// <summary>§2.3: `xs = cvttss2si((float)rect · invS)`; `ax0 = xs0 &amp; ~7`; `X1 = 8·trunc((xs1 + 7)/8)`; `x0c = ax0 − 8` unless `xs0 − ax0 &gt; 1`;
    /// `x1c = (X1 | 1) + 8` unless `(X1 | 1) − xs1 &gt; 1`; grid dims `(w &gt;&gt; 3) + 1`.</summary>
    public static TileGeometry Geometry(RectI rect, float invScale)
    {
        int xs0 = (int)((float)rect.X0 * invScale), ys0 = (int)((float)rect.Y0 * invScale);
        int xs1 = (int)((float)rect.X1 * invScale), ys1 = (int)((float)rect.Y1 * invScale);
        static int Ceil8(int v) { int t = v + 7; return (t + (t < 0 ? 7 : 0)) & ~7; }
        int ax0 = xs0 & ~7, ay0 = ys0 & ~7;
        int X1 = Ceil8(xs1), Y1 = Ceil8(ys1);
        int x0c = ax0 + ((xs0 - ax0) > 1 ? 0 : -8), y0c = ay0 + ((ys0 - ay0) > 1 ? 0 : -8);
        int x1c = (X1 | 1) + (((X1 | 1) - xs1) > 1 ? 0 : 8), y1c = (Y1 | 1) + (((Y1 | 1) - ys1) > 1 ? 0 : 8);
        int w = x1c - x0c, h = y1c - y0c;
        static int Div8(int v) => (v + ((v >> 31) & 7)) >> 3;   // signed w >> 3 with the compiler's sign fix (w is positive here)
        int gw = Div8(w) + 1, gh = Div8(h) + 1;
        var grid = new Image<Vec2I>(gw, gh);
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) grid.At(i, j) = new Vec2I(x0c + 8 * i, y0c + 8 * j);
        return new TileGeometry(x0c, y0c, x1c, y1c, grid);
    }

    public readonly record struct Projection(Image<Vec2I> Proj, int MinX, int MinY, int MaxX, int MaxY)
    {
        public int Bw => MaxX - MinX;
        public int Bh => MaxY - MinY;
        /// <summary>§4.1: the module is skipped unless `bw &gt; 0 &amp;&amp; minx != 0x7fffffff &amp;&amp; bh &gt; 0`.</summary>
        public bool Skipped => !(Bw > 0 && MinX != int.MaxValue && Bh > 0);
    }

    /// <summary>§4.1 (18043b5d0–18043b775): for every grid point inside the wide reference (`refW`×`refH`): `fx = (float)gx·sx`, `d = depth[(int)fy][(int)fx]`,
    /// `v = (((d·C2 + C3) + (fx·d)·C0) + (fy·d)·C1)` lane-wise, `inv = 1/v.z`, `py = v.y·inv`, `px = inv·v.x`, valid iff `−8 ≤ p &lt; (float)(dim + 7)`
    /// (NaN invalid), `proj = cvttss2si(p + 0.5f)`, bbox = signed min/max of the valid points. Invalid points hold `0x80000000` in both lanes.</summary>
    public static Projection Project(WarpField wf, Image<Vec2I> grid, int refW, int refH, int modW, int modH)
    {
        var depth = wf.Depth; int dStride = wf.DepthStride;
        if (depth.Length == 0) throw new InvalidOperationException("WarpField has no depth image");
        int gw = grid.Width, gh = grid.Height;
        var proj = new Image<Vec2I>(gw, gh);
        int minx = int.MaxValue, miny = int.MaxValue, maxx = int.MinValue, maxy = int.MinValue;
        float c00 = wf.M[0], c01 = wf.M[1], c02 = wf.M[2];       // C0 = column 0 (lanes x,y,z)
        float c10 = wf.M[4], c11 = wf.M[5], c12 = wf.M[6];       // C1
        float c20 = wf.M[8], c21 = wf.M[9], c22 = wf.M[10];      // C2
        float c30 = wf.M[12], c31 = wf.M[13], c32 = wf.M[14];    // C3
        float limW = (float)(modW + 7), limH = (float)(modH + 7);
        for (int j = 0; j < gh; j++)
        {
            for (int i = 0; i < gw; i++)
            {
                var g = grid.At(i, j); int gx = g.X, gy = g.Y;
                if (((gx | gy) >> 31) != 0 || gx >= refW || gy >= refH) { proj.At(i, j) = new Vec2I(Vec2I.Invalid, Vec2I.Invalid); continue; }
                float fx = (float)gx * wf.Sx, fy = (float)gy * wf.Sy;
                int dix = (int)fx, diy = (int)fy;                                   // cvttss2si, no clamping
                float d = depth[(long)diy * dStride + dix];
                float Y = fy * d, X = fx * d;
                float vx = ((d * c20 + c30) + X * c00) + Y * c10;
                float vy = ((d * c21 + c31) + X * c01) + Y * c11;
                float vz = ((d * c22 + c32) + X * c02) + Y * c12;
                float inv = 1.0f / vz;
                float py = vy * inv;
                if (!(py < limH) || !(py >= -8.0f)) { proj.At(i, j) = new Vec2I(Vec2I.Invalid, Vec2I.Invalid); continue; }   // ucomiss: NaN → invalid
                float px = inv * vx;
                if (!(px < limW) || !(px >= -8.0f)) { proj.At(i, j) = new Vec2I(Vec2I.Invalid, Vec2I.Invalid); continue; }
                int ix = (int)(px + 0.5f), iy = (int)(py + 0.5f);
                proj.At(i, j) = new Vec2I(ix, iy);
                if (ix < minx) minx = ix; if (iy < miny) miny = iy; if (ix > maxx) maxx = ix; if (iy > maxy) maxy = iy;
            }
        }
        return new Projection(proj, minx, miny, maxx, maxy);
    }

    /// <summary>§4.2: `Nm = ceil(16·scale)`, `s32i = (int)(32·scale)`, `hh = s32i &gt;&gt; 1`, `m1 = hh + Nm`, `m2 = m1 + 4`; the module render rect is
    /// `(minx − m2, miny − m2, maxx + 4 + m1, maxy + 4 + m1)`.</summary>
    public static RectI RenderRect(in Projection p, float scale)
    {
        int Nm = (int)MathF.Ceiling(scale * 16.0f);
        int s32i = (int)(scale * 32.0f);
        int hh = s32i >> 1, m1 = hh + Nm, m2 = m1 + 4;
        return new RectI(p.MinX - m2, p.MinY - m2, p.MaxX + 4 + m1, p.MaxY + 4 + m1);
    }
}
