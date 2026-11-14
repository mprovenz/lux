using System.Buffers.Binary;
using System.Text;
using Lux.Engine.Pipeline.Geometry;
using Lux.Engine.Pipeline.Registration;

namespace Lux.Engine.Session;

/// <summary>
/// Everything the renderer reads out of the registration chain (<see cref="StereoAsyncApi"/>), and nothing else — the analysis
/// cache's geometry + depth tier.
///
/// <para>These are the values that the level-0 render (<see cref="ExportBuild"/>) and every depth reader consume after the chain has
/// run: the reference module's online-calibration pair (<c>Pairs[ref]</c>, the aligned-calibration warp of every level); per
/// telephoto camera (the registration's higher group, <c>Pairs[id]</c> for each id in <c>Sizes</c>) its pair, its canvas size and
/// its CURRENT pose + slot after bundle adjustment (the level-0 warp field <c>TeleWarpFieldBuilder.BuildFromPoses</c>), with the
/// reference camera's pose + slot; the module names by id; and the upsampled full-frame depth (`UpsampleLayer` layer 6) that the
/// warp fields, the depth cache, `jpg+depth`, the depth map, the depth effect and the depth editor read. The stereo images, the
/// sparse matches, the dense cost volumes and the guide are intermediates of the chain only.</para>
///
/// <para>Every value is kept bit for bit (floats as their bit patterns), so a render from a deserialised geometry is the render
/// from the chain itself.</para>
/// </summary>
public sealed class CaptureGeometry
{
    /// <summary>One telephoto (higher-group) camera's registration result.</summary>
    public sealed record TeleCamera(int Id, string Name, CameraCalib PairFirst, CameraCalib PairSecond, (int W, int H) Size, ViewPose Pose, CalibDataFull Slot);

    public required int RefId { get; init; }
    /// <summary>Module name of every camera id in the capture (`api.Names`).</summary>
    public required IReadOnlyDictionary<int, string> Names { get; init; }
    /// <summary>`api.Pairs[ref]`: (view, module).</summary>
    public required (CameraCalib View, CameraCalib Module) RefPair { get; init; }
    public required ViewPose RefPose { get; init; }
    public required CalibDataFull RefSlot { get; init; }
    /// <summary>The higher group in id order (the keys of `api.Sizes`; a mono module is never in it).</summary>
    public required IReadOnlyList<TeleCamera> Tele { get; init; }
    /// <summary>The upsampled full-frame depth (metric mm, W0×H0); null when the chain produced none (no reference guide).</summary>
    public (float[] Depth, int W, int H)? FullDepth { get; init; }

    /// <summary>Take the geometry out of a registration state that has run the whole chain (states 1–7 and the upsample).</summary>
    public static CaptureGeometry FromRegistration(StereoAsyncApi api)
    {
        if (api.FullDepth is null) api.UpsampleFullDepth();
        int refId = api.RefId;
        var tele = api.Sizes.Keys.OrderBy(k => k).Select(id => new TeleCamera(id, api.Names[id], Clone(api.Pairs[id].First), Clone(api.Pairs[id].Second),
                                                                             api.Sizes[id], Clone(api.Cams[id].Pose), api.Cams[id].Slot.Clone())).ToList();
        return new CaptureGeometry
        {
            RefId = refId,
            Names = new Dictionary<int, string>(api.Names),
            RefPair = (Clone(api.Pairs[refId].First), Clone(api.Pairs[refId].Second)),
            RefPose = Clone(api.Cams[refId].Pose), RefSlot = api.Cams[refId].Slot.Clone(),
            Tele = tele,
            FullDepth = api.FullDepth is { } fd ? (fd.Depth, fd.W, fd.H) : null,
        };
    }

    /// <summary>
    /// The geometry a Lumen session file restores (its Stereo record): the ctor state of the capture
    /// (<see cref="StereoAsyncApi.Setup"/>) with the stored refined calibration put back (<see cref="StereoAsyncApi.RestoreCalibration"/>), and
    /// the depth map the session stored (the decoded ".dpc", 14-bit quantised) as the full-frame depth every depth reader — the level-0 warp
    /// fields included — then uses. No image is decoded and nothing of the registration chain runs.
    /// </summary>
    public static CaptureGeometry FromSession(Lux.Engine.Lri.LriFile lri, IReadOnlyDictionary<int, (float[] K, float[] R, float[] T)> calibration, (float[] Depth, int W, int H) depth)
    {
        var api = new StereoAsyncApi { Lri = lri }; api.Setup();
        api.RestoreCalibration(calibration);
        int refId = api.RefId;
        var tele = api.Sizes.Keys.OrderBy(k => k).Select(id => new TeleCamera(id, api.Names[id], Clone(api.Pairs[id].First), Clone(api.Pairs[id].Second),
                                                                             api.Sizes[id], Clone(api.Cams[id].Pose), api.Cams[id].Slot.Clone())).ToList();
        return new CaptureGeometry
        {
            RefId = refId, Names = new Dictionary<int, string>(api.Names),
            RefPair = (Clone(api.Pairs[refId].First), Clone(api.Pairs[refId].Second)),
            RefPose = Clone(api.Cams[refId].Pose), RefSlot = api.Cams[refId].Slot.Clone(),
            Tele = tele, FullDepth = depth,
        };
    }

    static CameraCalib Clone(CameraCalib c) => c with { K = (float[])c.K.Clone(), T = (float[])c.T.Clone(), R = (float[])c.R.Clone() };
    static ViewPose Clone(ViewPose p) => new() { P = (float[])p.P.Clone(), U = (float[])p.U.Clone(), Q = (float[])p.Q.Clone(), Scale1 = p.Scale1, Shift1 = p.Shift1, Scale2 = p.Scale2, Shift2 = p.Shift2, Shift3 = p.Shift3, Scale3 = p.Scale3 };

    // ------------------------------------------------------------------------------------------------ serialisation
    // Little-endian; floats as raw bit patterns. The depth plane is a separate block (it is 99.9 % of the bytes and compresses on
    // its own as 32-bit values); <see cref="SerializeHeader"/> carries everything else.

    const uint Magic = 0x4F45474C;   // "LGEO"
    const int FormatVersion = 1;

    public byte[] SerializeHeader()
    {
        var w = new Writer();
        w.U32(Magic); w.I32(FormatVersion); w.I32(RefId);
        w.I32(Names.Count); foreach (var kv in Names.OrderBy(k => k.Key)) { w.I32(kv.Key); w.Str(kv.Value); }
        w.Cam(RefPair.View); w.Cam(RefPair.Module); w.Pose(RefPose); w.Slot(RefSlot);
        w.I32(Tele.Count);
        foreach (var t in Tele) { w.I32(t.Id); w.Str(t.Name); w.Cam(t.PairFirst); w.Cam(t.PairSecond); w.I32(t.Size.W); w.I32(t.Size.H); w.Pose(t.Pose); w.Slot(t.Slot); }
        if (FullDepth is { } fd) { w.I32(1); w.I32(fd.W); w.I32(fd.H); } else w.I32(0);
        return w.ToArray();
    }

    /// <summary>The depth plane's raw bytes (empty when there is no depth).</summary>
    public byte[] SerializeDepth()
    {
        if (FullDepth is not { } fd) return Array.Empty<byte>();
        var b = new byte[fd.Depth.Length * 4];
        Buffer.BlockCopy(fd.Depth, 0, b, 0, b.Length);
        return b;
    }

    /// <summary>Rebuild from <see cref="SerializeHeader"/> + <see cref="SerializeDepth"/>. Throws <see cref="InvalidDataException"/> on any
    /// inconsistency (the store has already verified the checksums; this guards the format).</summary>
    public static CaptureGeometry Deserialize(byte[] header, byte[]? depth)
    {
        var r = new Reader(header);
        if (r.U32() != Magic) throw new InvalidDataException("not a geometry block");
        int fv = r.I32(); if (fv != FormatVersion) throw new InvalidDataException($"geometry block format {fv}, expected {FormatVersion}");
        int refId = r.I32();
        int nn = r.I32(); var names = new Dictionary<int, string>();
        for (int i = 0; i < nn; i++) { int id = r.I32(); names[id] = r.Str(); }
        var rv = r.Cam(); var rm = r.Cam(); var rp = r.Pose(); var rs = r.Slot();
        int nt = r.I32(); var tele = new List<TeleCamera>();
        for (int i = 0; i < nt; i++) { int id = r.I32(); string n = r.Str(); var a = r.Cam(); var b = r.Cam(); int sw = r.I32(), sh = r.I32(); tele.Add(new TeleCamera(id, n, a, b, (sw, sh), r.Pose(), r.Slot())); }
        (float[], int, int)? fd = null;
        if (r.I32() == 1)
        {
            int w = r.I32(), h = r.I32();
            if (depth is null || depth.Length != (long)w * h * 4) throw new InvalidDataException("geometry depth plane missing or of the wrong size");
            var d = new float[w * h]; Buffer.BlockCopy(depth, 0, d, 0, depth.Length); fd = (d, w, h);
        }
        if (!r.AtEnd) throw new InvalidDataException("trailing bytes in a geometry block");
        return new CaptureGeometry { RefId = refId, Names = names, RefPair = (rv, rm), RefPose = rp, RefSlot = rs, Tele = tele, FullDepth = fd };
    }

    sealed class Writer
    {
        readonly MemoryStream _ms = new();
        readonly byte[] _b = new byte[8];
        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_b, v); _ms.Write(_b, 0, 4); }
        public void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(_b, v); _ms.Write(_b, 0, 4); }
        public void F(float v) => U32(BitConverter.SingleToUInt32Bits(v));
        public void Fs(float[] v) { I32(v.Length); foreach (var x in v) F(x); }
        public void Str(string s) { var b = Encoding.UTF8.GetBytes(s); I32(b.Length); _ms.Write(b); }
        public void Cam(CameraCalib c) { Fs(c.K); Fs(c.T); Fs(c.R); F(c.ViewOffX); F(c.ViewOffY); F(c.CropX); F(c.CropY); }
        public void Pose(ViewPose p)
        {
            Fs(p.P); Fs(p.U); Fs(p.Q);
            foreach (var (x, y) in new[] { p.Scale1, p.Shift1, p.Scale2, p.Shift2, p.Shift3, p.Scale3 }) { F(x); F(y); }
        }
        public void Slot(CalibDataFull s) { Fs(s.K); Fs(s.T); Fs(s.R); Fs(s.K2); F(s.OffX); F(s.OffY); F(s.ScX); F(s.ScY); Fs(s.Dist); }
        public byte[] ToArray() => _ms.ToArray();
    }

    sealed class Reader
    {
        readonly byte[] _d; int _p;
        public Reader(byte[] d) { _d = d; }
        public bool AtEnd => _p == _d.Length;
        void Need(int n) { if (_p + n > _d.Length) throw new InvalidDataException("geometry block is truncated"); }
        public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(_p)); _p += 4; return v; }
        public int I32() { Need(4); int v = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(_p)); _p += 4; return v; }
        public float F() => BitConverter.UInt32BitsToSingle(U32());
        public float[] Fs() { int n = I32(); if (n < 0 || n > 4096) throw new InvalidDataException("bad array length in a geometry block"); var v = new float[n]; for (int i = 0; i < n; i++) v[i] = F(); return v; }
        public string Str() { int n = I32(); if (n < 0 || n > 256) throw new InvalidDataException("bad string in a geometry block"); Need(n); var s = Encoding.UTF8.GetString(_d, _p, n); _p += n; return s; }
        public CameraCalib Cam() { var k = Fs(); var t = Fs(); var r = Fs(); return new CameraCalib(k, t, r, F(), F(), F(), F()); }
        public ViewPose Pose()
        {
            var p = new ViewPose { P = Fs(), U = Fs(), Q = Fs() };
            p.Scale1 = (F(), F()); p.Shift1 = (F(), F()); p.Scale2 = (F(), F()); p.Shift2 = (F(), F()); p.Shift3 = (F(), F()); p.Scale3 = (F(), F());
            return p;
        }
        public CalibDataFull Slot() => new() { K = Fs(), T = Fs(), R = Fs(), K2 = Fs(), OffX = F(), OffY = F(), ScX = F(), ScY = F(), Dist = Fs() };
    }
}
