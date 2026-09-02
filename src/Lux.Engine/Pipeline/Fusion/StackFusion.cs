using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ltpb;
using Lux.Engine.Imaging;
using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Color;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Isp.Stages;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// `lt::StackFusion(Image&lt;float&gt;&amp; out, Image&lt;float&gt;&amp; gainOut, vector&lt;shared_ptr&lt;CapturedImage&gt;&gt; const&amp; frames, int refIndex, float k, Vec3&lt;float&gt; const&amp; neutral)`
/// (`180203c90`) and its driver `FUN_18020b0b0` — the <b>stacked-capture</b> fusion: one module, N frames, merged into a single
/// float Bayer frame plus a uint8 "gain map" that the module ISP then consumes as its STD plane. Spec `a-stack-fusion.md`.
///
/// It is the same wavelet block merge as <see cref="PackedBayerFusion"/> (`FUN_1801e6a20`, <see cref="BayerWavelet"/> /
/// <see cref="BayerMerge"/>) with the stack's other frames in place of the other modules, and it differs in exactly these ways:
/// <list type="bullet">
/// <item>frame preparation is `FUN_1802067f0` = hot-pixel patch + `RestoreHighlightsBayer` and <b>nothing else</b> — no black
/// subtraction, no exposure gain, no vignetting (all frames share the module's optics, so there is nothing to equalise);</item>
/// <item>the flow's collapsed images do <b>not</b> get the sqrt LUT (`FUN_1801d6c80` is called only from
/// `ColorFusionBayer::initialize`), and the flow runs with an empty validity functor;</item>
/// <item>the packed images are `PackBayerImageProtoType&lt;vec4x16ui, unsigned short&gt;` of the raw frame, normalised to
/// `(v − black_i)/(white_i − black_i)` halves by `FUN_180206960` — per frame with that frame's own black/white;</item>
/// <item>the noise functor (`180207880`) is <see cref="PackedBayerFusion.NoiseFn"/> without the vignetting-map factor, over a
/// block-mean-reciprocal image computed on the packed <b>raw</b> quads (`FUN_1801d6e30`);</item>
/// <item>`cN` is 1.0 (not `gain/NStack`), and the result gets the reference black added back (`FUN_180207930`) with no
/// vignetting re-applied.</item>
/// </list>
/// The colour path only: a mono module would take `FUN_180202120` / `StackFusion::lambda_0`, which no export reaches (the
/// reference module is always colour) and which is left unported rather than guessed.
/// </summary>
public sealed class StackFusion
{
    static readonly float One = BitConverter.Int32BitsToSingle(0x3f800000);     // DAT_180681c78
    static readonly float NoiseMul = BitConverter.Int32BitsToSingle(0x41000000);// DAT_180685d4c = 8.0 (inside FUN_1801e6a20)
    static readonly float Eps = BitConverter.Int32BitsToSingle(0x3727c5ac);     // DAT_18068b2e0 = 1e-5
    static readonly float Tenth = BitConverter.Int32BitsToSingle(0x3dcccccd);   // DAT_1806aeb30 = 0.1

    /// <summary>`DAT_1806b5518 = {0.25f, 1.0f}`, indexed `stream+0x14 == 0 ? 1 : 0` (`18020b34c–18020b35a`): the noise-model
    /// scale `param_5`, 1.0 for a full-scale surface and 0.25 for a half-scale one — the same `k` the `ColorFusionBayer`
    /// constructor applies.</summary>
    public static float NoiseScaleK(bool halfScale) => halfScale ? BitConverter.Int32BitsToSingle(0x3e800000) : One;

    readonly LriFile _lri;
    public string Module { get; }
    public int NStack { get; }
    /// <summary>`stream+0x10`, the stack's reference frame index. `FUN_1804b2fa0` builds the stream with `r8d = 0`, so it is
    /// always 0 in a render; `FUN_18020b0b0` passes it to `lt::StackFusion` as `ref_index` ("Incorrect ref_index!").</summary>
    public int RefIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public float Black { get; }        // the reference frame's CapturedImage+0xb4
    public float White { get; }        // CapturedImage+0xb8
    public float[] A { get; } = new float[4];
    public float[] B { get; } = new float[4];
    public Action<string>? Log { get; }

    /// <summary>The reference frame as the module ISP sees it (`FUN_180110190(hdr, 0, camId)`): its raw ushort buffer feeds the
    /// Stats (AWB / IR blend) exactly as in a single-frame render, and its `FrameBlack` is <see cref="Black"/>.</summary>
    public CapturedFrame RefFrame { get; }

    // packed half images (4 lanes per packed pixel, quad order TR, TL, BL, BR) of (raw − black)/(white − black)
    ushort[] _normRef = null!;
    readonly List<ushort[]> _normSrc = new();
    readonly List<Vec2S[]> _flows = new();
    readonly List<(int W, int H)> _flowDims = new();
    Vec4F[] _blockRcp = null!;
    int _wp, _hp, _brW, _brH;

    /// <summary>The fused full-resolution float Bayer frame (`StackFusion` out, black added back) — what
    /// `FUN_18020a6d0` hands the module ISP as its BayerFloat source.</summary>
    public float[] Fused { get; private set; } = null!;
    /// <summary>The uint8 gain map (`FUN_1802092b0(weight)`) `FUN_18020b870` returns; `FUN_180209010` turns it into the STD
    /// plane with `k = 1.0` (`1804d8c50`: `movss xmm2, [0x180681c78]`).</summary>
    public byte[] Gain8 { get; private set; } = null!;

    public StackFusion(LriFile lri, string moduleName, float cct, float tint, Action<string>? log = null, int refIndex = 0)
    {
        _lri = lri; Module = moduleName; Log = log; RefIndex = refIndex;
        var frames = lri.Frames[moduleName];
        NStack = frames.Count;
        if (NStack < 2) throw new InvalidOperationException("Stack must have at least two images!");
        if (refIndex < 0 || refIndex >= NStack) throw new ArgumentOutOfRangeException(nameof(refIndex), "Incorrect ref_index!");

        // FUN_18020b0b0: the neutral comes from a per-frame SoftISP on FRAME 0 (`FUN_18020aad0`: highlight_restore none,
        // manual_temp at the capture (CCT, tint) through the camera's own colour profile → Stats neutral) and is then used
        // both for the per-frame black estimate `FUN_180125d10(frame, neutral, 42.0, 1.2, 40)` and for RestoreHighlightsBayer.
        var profile = PackedBayerFusion.CameraProfile(lri, frames[0].Module.Id);
        var neutral = WhiteBalance.NeutralFromTempTint(cct, tint, profile);

        RefFrame = CapturedFrame.Load(lri, frames[refIndex]);
        var info = RefFrame.Info;
        if (!info.IsColour) throw new NotSupportedException("mono stack fusion (StackFusion::lambda_0 / FUN_180202120) is not ported");
        var noise = info.Noise ?? throw new InvalidOperationException($"{moduleName}: no sensor noise model");
        if (info.HasHotPixelLeakageCalibration) throw new NotSupportedException("HotpixelCalibration::correctHotpixelLeakage is not ported");
        Width = RefFrame.Width; Height = RefFrame.Height;
        White = noise.White;
        Black = float.IsNaN(info.FrameBlack) ? noise.Black : info.FrameBlack;

        // 180203c90 L~355: A = (aR, aG, aB, aG)·k, B = (bR, bG, bB, bG)·k of the REFERENCE frame's model
        // (`FUN_180120cb0(cap+0xb0, cap+0x40)`, entries 0x38/0x70/0xa8 and 0x3c/0x74/0xac).
        float k = NoiseScaleK(info.IsHalfScale);
        var m = noise.ModelForGain(info.AnalogGain);
        A[0] = m.R.A * k; A[1] = m.G.A * k; A[2] = m.Bl.A * k; A[3] = m.G.A * k;
        B[0] = m.R.B * k; B[1] = m.G.B * k; B[2] = m.Bl.B * k; B[3] = m.G.B * k;

        Initialize(frames, neutral);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // FUN_1802067f0 — the per-frame preparation: hot-pixel patch, then (colour only) highlight restore. No black, no gain.
    // ---------------------------------------------------------------------------------------------------------------------------------
    (ushort[] Prep, float Black, float White) PrepareFrame(LriFile.ModuleRef mref, float[] neutral)
    {
        var f = CapturedFrame.Load(_lri, mref);
        int w = f.Width, h = f.Height;
        if (w != Width || h != Height) throw new InvalidOperationException("stack frames must all have the module's frame size");
        var noise = f.Info.Noise ?? throw new InvalidOperationException("stack frame has no sensor noise model");
        float black = float.IsNaN(f.Info.FrameBlack) ? noise.Black : f.Info.FrameBlack;   // CapturedImage+0xb4 (FUN_180125d10)
        var red = f.Module.SensorBayerRedOverride;
        int rx = red?.X ?? 0, ry = red?.Y ?? 0;
        var full = new RectI(0, 0, w, h);
        var lut = noise.SigmaTables(f.Info.AnalogGain);
        var hp = new ushort[w * h];
        HotPixelKernel.RunInto(f.Raw, w, h, full, rx, ry, One, lut[0], lut[1], lut[2], hp, w, 0);   // FUN_18039f640
        if (!f.Info.IsColour) return (hp, black, noise.White);
        var hr = new ushort[w * h];
        HighlightRestoreKernel.Run(hp, w, 0, full, hr, w, 0, full, rx, ry, neutral, black, noise.White);
        return (hr, black, noise.White);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // PackBayerImageProtoType<vec4x16ui, unsigned short> (1801e6470, lambda_2 1801e9be0) + FUN_180206960 (normalise to halves)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>`PackBayerImageProtoType&lt;vec4x16ui, unsigned short&gt;` immediately followed by `FUN_180206960`
    /// (`out = ((float)v − black)·inv`, stored through the `FUN_18001f080(0xf, 0x10)` converter, which tail-calls the software
    /// RTZ float→half `FUN_1800e8150`): output `(W/2, H/2)` of four halves per pixel in quad order
    /// `(p[2y][2x+1], p[2y][2x], p[2y+1][2x], p[2y+1][2x+1])`.</summary>
    static ushort[] PackNormalized(ushort[] src, int W, int H, float black, float inv, out int wp, out int hp)
    {
        if (((W | H) & 1) != 0) throw new InvalidOperationException("src needs to be even.");
        wp = W >> 1; hp = H >> 1;
        var o = new ushort[wp * hp * 4];
        for (int y = 0; y < hp; y++)
        {
            int r0 = (2 * y) * W, r1 = r0 + W;
            for (int x = 0; x < wp; x++)
            {
                int q = (y * wp + x) * 4;
                o[q] = Half16.FromFloat(((float)src[r0 + 2 * x + 1] - black) * inv);
                o[q + 1] = Half16.FromFloat(((float)src[r0 + 2 * x] - black) * inv);
                o[q + 2] = Half16.FromFloat(((float)src[r1 + 2 * x] - black) * inv);
                o[q + 3] = Half16.FromFloat(((float)src[r1 + 2 * x + 1] - black) * inv);
            }
        }
        return o;
    }

    /// <summary>`FUN_1801d6e30`: the packed-quad twin of `FUN_1801d7140` (<see cref="PackedBayerFusion.BlockMeanRcp"/>) —
    /// `(ceil(Wp/8), ceil(Hp/8))` vec4 image, per 8×8 packed block `inv = 1/(validRows·validCols)` times the sum of
    /// `rcpps(maxps((float)quad, 0.1))` over the valid quads. The quads are the RAW packed uint16 values, so this block mean is in
    /// DN including the black level — which is why the noise functor adds `black` to the reciprocal.</summary>
    static Vec4F[] BlockMeanRcpPacked(ushort[] packed, int wp, int hp, out int bw, out int bh)
    {
        bw = (wp + 7) / 8; bh = (hp + 7) / 8;
        var o = new Vec4F[bw * bh];
        var tenth = Vector128.Create(Tenth);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int vc = 8 - Math.Max(0, (bx + 1) * 8 - wp);
                int vr = 8 - Math.Max(0, by * 8 + 8 - hp);
                float inv = One / (float)(vr * vc);
                float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
                if (vr > 0)
                {
                    for (int y = 0; y < vr; y++)
                    {
                        int row = ((by * 8 + y) * wp + bx * 8) * 4;
                        for (int x = 0; x < vc; x++)
                        {
                            int p = row + x * 4;
                            var v = Vector128.Create((float)packed[p], (float)packed[p + 1], (float)packed[p + 2], (float)packed[p + 3]);
                            v = Sse.Reciprocal(Sse.Max(v, tenth));
                            s0 += v.GetElement(0); s1 += v.GetElement(1); s2 += v.GetElement(2); s3 += v.GetElement(3);
                        }
                    }
                }
                o[by * bw + bx] = new Vec4F(inv * s0, inv * s1, inv * s2, inv * s3);
            }
        return o;
    }

    /// <summary>`StackFusion::lambda_3` (`180207880`, disasm 1802078a0–1802078f1): `v = Mean2x2Vec(blockRcp, pos)` (`1801d76c0`);
    /// `r = rcpps(v); d = (1 − v·r)·r; s = ((black + r) + d)·A; s = (1/white)·s; s = s + B; s = maxps(s, 1e-5); return white²·s`.
    /// Identical to `ColorFusionBayer`'s `initialize::lambda_1` except for the missing `f²` vignetting-map factor.</summary>
    public Vec4F NoiseFn(int bx, int by)
    {
        var v = PackedBayerFusion.Mean2x2Vec(_blockRcp, _brW, _brH, _brW, bx, by);
        var r = Sse.Reciprocal(v);
        var d = (Vector128.Create(One) - v * r) * r;
        var s = Vector128.Create(Black) + r;
        s = s + d;
        s = s * Vector128.Create(A[0], A[1], A[2], A[3]);
        s = Vector128.Create(One / White) * s;
        s = s + Vector128.Create(B[0], B[1], B[2], B[3]);
        s = Sse.Max(s, Vector128.Create(Eps));
        var o = Vector128.Create(White * White) * s;
        return new Vec4F(o.GetElement(0), o.GetElement(1), o.GetElement(2), o.GetElement(3));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // 180203c90 body
    // ---------------------------------------------------------------------------------------------------------------------------------
    void Initialize(IReadOnlyList<LriFile.ModuleRef> frames, float[] neutral)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (refPrep, refBlack, refWhite) = PrepareFrame(frames[RefIndex], neutral);
        if (refBlack != Black || refWhite != White) throw new InvalidOperationException("reference frame black/white mismatch");

        // FastCollapse of the prepared ushort frame — with NO sqrt LUT (1801d6c80 is called only from ColorFusionBayer::initialize)
        var refCollapsed = BlockFlow.FastCollapse(refPrep, Width, Height, out int wc, out int hc);
        const int levels = 4;   // (redx|redy) >> 31 | 4 — 4 for a colour module, 5 for a mono one
        var refPyr = BlockFlow.Pyramid(refCollapsed, wc, hc, levels, out var pdims);

        float range = White - Black;
        _normRef = PackNormalized(refPrep, Width, Height, Black, One / range, out _wp, out _hp);
        var packedRefRaw = PackRaw(refPrep, Width, Height);
        _blockRcp = BlockMeanRcpPacked(packedRefRaw, _wp, _hp, out _brW, out _brH);
        packedRefRaw = null!;
        refPrep = null!;

        for (int i = 0; i < frames.Count; i++)
        {
            if (i == RefIndex) continue;
            var (prep, black, white) = PrepareFrame(frames[i], neutral);   // black/white per frame: FUN_180125630(frames[i]) +4/+8
            var col = BlockFlow.FastCollapse(prep, Width, Height, out int sw2, out int sh2);
            var flow = BlockFlow.ComputeFlow(refPyr, pdims, col, sw2, sh2, levels, null, out int fw, out int fh);
            _flows.Add(flow); _flowDims.Add((fw, fh));
            _normSrc.Add(PackNormalized(prep, Width, Height, black, One / (white - black), out _, out _));
            Log?.Invoke($"stack fusion {Module}: frame {i} black {black:R} flow {fw}x{fh}");
        }
        Log?.Invoke($"stack fusion {Module}: {NStack} frames, ref {RefIndex}, black {Black:R} white {White:R}, prep+flow {sw.Elapsed.TotalSeconds:F1}s");

        // Tiler::Run over 256×256 tiles of the packed output (local_1d0 = 0x10000000100), each tile an independent
        // FUN_1801e6a20 on views of the two output images — so the tiles can run in any order.
        var fused = new Vec4F[_wp * _hp];
        var weight = new Vec4F[_wp * _hp];
        var tiles = new List<RectI>();
        for (int ty = 0; ty < _hp; ty += 256)
            for (int tx = 0; tx < _wp; tx += 256)
                tiles.Add(new RectI(tx, ty, Math.Min(tx + 256, _wp), Math.Min(ty + 256, _hp)));
        System.Threading.Tasks.Parallel.ForEach(tiles, t =>
        {
            var (o, w, tw, th) = Merge(t, One, range);
            for (int y = 0; y < th; y++)
            {
                Array.Copy(o, y * tw, fused, (t.Y0 + y) * _wp + t.X0, tw);
                Array.Copy(w, y * tw, weight, (t.Y0 + y) * _wp + t.X0, tw);
            }
        });
        Log?.Invoke($"stack fusion {Module}: merged {_wp}x{_hp} packed in {tiles.Count} tiles ({sw.Elapsed.TotalSeconds:F1}s)");

        // FUN_180207930: + black on all four lanes; FUN_1801e67b0 / FUN_1801e6900 unpack to full resolution
        var blackV = Vector128.Create(Black);
        for (int i = 0; i < fused.Length; i++)
        {
            var v = Vector128.Create(fused[i].R, fused[i].G, fused[i].B, fused[i].A) + blackV;
            fused[i] = new Vec4F(v.GetElement(0), v.GetElement(1), v.GetElement(2), v.GetElement(3));
        }
        Fused = PackedBayerFusion.Unpack(fused, _wp, _hp);
        Gain8 = PackedBayerFusion.WeightToByte(PackedBayerFusion.UnpackWeight(weight, _wp, _hp));   // FUN_1802092b0 in FUN_18020b0b0
        _normRef = null!; _normSrc.Clear();
        Log?.Invoke($"stack fusion {Module}: done in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>`PackBayerImageProtoType&lt;vec4x16ui, unsigned short&gt;` alone (the input of `FUN_1801d6e30`).</summary>
    static ushort[] PackRaw(ushort[] src, int W, int H)
    {
        int wp = W >> 1, hp = H >> 1;
        var o = new ushort[wp * hp * 4];
        for (int y = 0; y < hp; y++)
        {
            int r0 = (2 * y) * W, r1 = r0 + W;
            for (int x = 0; x < wp; x++)
            {
                int q = (y * wp + x) * 4;
                o[q] = src[r0 + 2 * x + 1]; o[q + 1] = src[r0 + 2 * x]; o[q + 2] = src[r1 + 2 * x]; o[q + 3] = src[r1 + 2 * x + 1];
            }
        }
        return o;
    }

    static int CeilDiv(int v, int d) => v >= 0 ? (v + d - 1) / d : -((-v) / d);
    static int FloorDiv(int v, int d) => v >= 0 ? v / d : -(((-v) + d - 1) / d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vector128<float> Load(Vec4F v) => Vector128.Create(v.R, v.G, v.B, v.A);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static Vec4F Store(Vector128<float> v) => new(v.GetElement(0), v.GetElement(1), v.GetElement(2), v.GetElement(3));

    /// <summary>The merge kernel `FUN_1801e6a20(out, wOut, ref, srcs, flows, noiseFn, roi, cN, range)` — the same body as
    /// <see cref="PackedBayerFusion.Merge"/>, over the stack's frames and with this class's noise functor.</summary>
    public (Vec4F[] Fused, Vec4F[] Weight, int W, int H) Merge(RectI halfRoi, float cN, float range)
    {
        cN = cN * NoiseMul;
        int rx0 = halfRoi.X0, ry0 = halfRoi.Y0, rx1 = halfRoi.X1, ry1 = halfRoi.Y1;
        if (!(rx0 < rx1 && ry0 < ry1)) { rx0 = 0; ry0 = 0; rx1 = _wp; ry1 = _hp; }
        int rw = rx1 - rx0, rh = ry1 - ry0;
        var refImg = new Vec4F[rw * rh];
        var rangeV = Vector128.Create(range);
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int p = ((y + ry0) * _wp + (x + rx0)) * 4;
                var v = Vector128.Create(Half16.ToFloat(_normRef[p]), Half16.ToFloat(_normRef[p + 1]), Half16.ToFloat(_normRef[p + 2]), Half16.ToFloat(_normRef[p + 3]));
                refImg[y * rw + x] = Store(v * rangeV);
            }
        var wImg = new Vec4F[rw * rh];
        var hann = BayerMerge.Hann;
        var R = new Vec4F[256]; var Rw = new Vec4F[256]; var S = new Vec4F[256]; var acc = new Vec4F[256];
        var oneV = Vector128.Create(One);
        int byStart = FloorDiv(ry0, 8) * 8 - 8, byEnd = CeilDiv(ry1, 8) * 8 + 8;
        int bxStart = FloorDiv(rx0, 8) * 8 - 8, bxEnd = CeilDiv(rx1, 8) * 8 + 8;
        int nSrc = _normSrc.Count;
        for (int by = byStart; by < byEnd; by += 8)
        {
            int bpy = by / 8;
            int fyc = Math.Max(bpy, 0);
            for (int bx = bxStart; bx < bxEnd; bx += 8)
            {
                if (!(Math.Max(bx, 0) < Math.Min(bx + 16, _wp) && Math.Max(by, 0) < Math.Min(by + 16, _hp))) continue;
                int bpx = bx / 8;
                int fxc = Math.Max(bpx, 0);
                var noise = NoiseFn(bpx, bpy);
                BayerMerge.ExtractBlock(_normRef, _wp, _hp, bx, by, range, R);
                Array.Copy(R, Rw, 256);
                BayerWavelet.Forward(Rw);
                Array.Clear(acc);
                var cnt = oneV; var q2 = Vector128<float>.Zero;
                for (int i = 0; i < nSrc; i++)
                {
                    var (fw, fh) = _flowDims[i];
                    int fy = Math.Min(fyc, fh - 1), fx = Math.Min(fxc, fw - 1);
                    var fl = _flows[i][fy * fw + fx];
                    int sx = bx + fl.X, sy = by + fl.Y;
                    if (!(Math.Max(sx, 0) < Math.Min(sx + 16, _wp) && Math.Max(sy, 0) < Math.Min(sy + 16, _hp)))
                    {
                        for (int k = 0; k < 256; k++) acc[k] = Store(Load(acc[k]) + Load(R[k]));
                        cnt = cnt + oneV;
                        continue;
                    }
                    BayerMerge.ExtractBlock(_normSrc[i], _wp, _hp, sx, sy, range, S);
                    BayerWavelet.Forward(S);
                    var nz = new Vec4F(noise.R * cN, noise.G * cN, noise.B * cN, noise.A * cN);
                    var q = Load(BayerMerge.Shrink(S, Rw, nz));
                    BayerWavelet.Inverse(S);
                    for (int k = 0; k < 256; k++) acc[k] = Store(Load(acc[k]) + Load(S[k]));
                    cnt = (cnt + oneV) - q;
                    q2 = q2 + q * q;
                }
                BayerMerge.AddHann(refImg, rw, rh, bx - rx0, by - ry0, acc, hann);
                var s = cnt * cnt + q2;
                BayerMerge.AddHannScalar(wImg, rw, rh, bx - rx0, by - ry0, Store(s), hann);
            }
        }
        float N = (float)(nSrc + 1);
        float rN = BayerMerge.RcpNR(N);
        for (int i = 0; i < refImg.Length; i++) { var v = refImg[i]; refImg[i] = new Vec4F(v.R * rN, v.G * rN, v.B * rN, v.A * rN); }
        long n2 = (long)(nSrc + 1) * (nSrc + 1);
        float rN2 = BayerMerge.RcpNR((float)(ulong)n2);
        for (int i = 0; i < wImg.Length; i++) { var v = wImg[i]; wImg[i] = new Vec4F(v.R * rN2, v.G * rN2, v.B * rN2, v.A * rN2); }
        return (refImg, wImg, rw, rh);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // What ReferenceImageCache::processLevel needs (1804d86e0 L145–430)
    // ---------------------------------------------------------------------------------------------------------------------------------
    /// <summary>The stacked float Bayer as an image over the whole frame (`FUN_18020a6d0`).</summary>
    public Image<float> BayerImage() => new(new RectI(0, 0, Width, Height), Fused, Width, 0);

    /// <summary>`FUN_180209010(out, gainView, k)` with `k = 1.0` (`1804d8c50`): `std = DAT_1806b5110[gain8]·k` over
    /// <paramref name="rect"/>.</summary>
    public Image<float> StdImage(RectI rect)
    {
        var o = new Image<float>(rect);
        for (int y = rect.Y0; y < rect.Y1; y++)
        {
            var row = o.Row(y - rect.Y0);
            int src = y * Width;
            for (int x = rect.X0; x < rect.X1; x++) row[x - rect.X0] = PackedBayerFusion.StdTable[Gain8[src + x]];
        }
        return o;
    }
}
