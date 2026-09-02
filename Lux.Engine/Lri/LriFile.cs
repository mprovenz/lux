using System.Runtime.Intrinsics;
using System.Buffers.Binary;
using Ltpb;

namespace Lux.Engine.Lri;

/// <summary>
/// Parses a Light L16 <c>.lri</c> container: a chain of <c>LELR</c> blocks, each 32-byte header
/// (magic, u64 block-length, u64 msg-offset, u32 msg-len, u8 type) followed by a protobuf message.
/// Type-0 messages are partial <see cref="LightHeader"/>s that are merged together; per-module raw
/// frames (RAW_PACKED_10BPP) live at <c>blockBase + surface.DataOffset</c>. Mirrors the verified
/// Python <c>lri_meta.py</c>.
/// </summary>
public sealed class LriFile
{
    public byte[] Data { get; }
    public LightHeader Header { get; }
    /// <summary>Module id (e.g. "A1", "B4") → the module message and the file offset of the block it came from — the
    /// module's <b>frame 0</b>, which is the frame every consumer that wants "the" capture of a module means: the renderer
    /// builds its stream with frame index 0 (`FUN_1804b2fa0` passes `r8d = 0` to the ctor `1802095e0`, which stores it at
    /// `stream+0x10`), and `FUN_180110190(hdr, &amp;cap, 0, camId)` is what every single-frame path resolves. For the 391
    /// single-frame captures every block already has `frame_index == 0`, so this is exactly the previous "last block
    /// defining a module wins" — the last block of frame 0 still wins, which is what makes a header split across several
    /// partial type-0 blocks merge the same way. <see cref="Frames"/> reaches the other frames of a stack.</summary>
    public IReadOnlyDictionary<string, ModuleRef> Modules { get; }

    /// <summary>Module id → its blocks indexed by <c>frame_index</c> (`FUN_180110190(hdr, out, frameIdx, camId)`), one entry
    /// per frame; length 1 for an ordinary capture and <see cref="StackFrames"/> for a stacked one. Later blocks with the
    /// same (id, frame) replace earlier ones, the same last-wins merge <see cref="Modules"/> uses.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ModuleRef>> Frames { get; }
    /// <summary>The STANDALONE type-1 <c>ViewPreferences</c> blocks (merged in file order). These — not the header's
    /// <c>view_preferences</c> copy — carry <c>image_gain / image_integration_time_ns / display_*</c>, which Lumen
    /// uses for BaselineExposure / ev_offset and the Exif exposure (SoT §3.7). Null if the file has none.</summary>
    public ViewPreferences? ViewPreferencesBlock { get; }
    /// <summary>The standalone type-2 <c>GPSData</c> block, if present.</summary>
    public GPSData? GpsBlock { get; }

    /// <summary>`FUN_180112250(cameraList)` = <c>cameraList+0x28</c>, the frames per stack: 1 for an ordinary capture,
    /// 4 for the stacked ones (the camera writes one module block per frame per module). Lumen keys several decisions on
    /// it — `lt::StackFusion` runs when it is ≥ 2 (`FUN_18020a6d0`/`FUN_18020b0b0`), the gain map only exists then
    /// ("Gain map not available in non-stack mode."), and the module ISP drops hot-pixel/highlight-restore
    /// (`FUN_18050cc30` L597) because the stack fusion already applied them per frame.</summary>
    public int StackFrames { get; }

    /// <summary>Every module block in the file, in file order — one per module per frame, so a 4-frame stack appears
    /// four times. <see cref="Modules"/> keeps only frame 0 of each module and <see cref="Frames"/> indexes them by frame;
    /// this list preserves the raw file order (surface verification, `mod-info`).</summary>
    public IReadOnlyList<ModuleRef> ModuleBlocks { get; }

    public readonly record struct ModuleRef(CameraModule Module, long BlockBase);

    private LriFile(byte[] data, LightHeader header, IReadOnlyDictionary<string, ModuleRef> modules, IReadOnlyDictionary<string, IReadOnlyList<ModuleRef>> frames,
                    IReadOnlyList<ModuleRef> blocks, ViewPreferences? vp, GPSData? gps, int stackFrames)
        => (Data, Header, Modules, Frames, ModuleBlocks, ViewPreferencesBlock, GpsBlock, StackFrames) = (data, header, modules, frames, blocks, vp, gps, stackFrames);

    public static LriFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var header = new LightHeader();
        var perFrame = new Dictionary<string, SortedDictionary<uint, ModuleRef>>();
        var blocks = new List<ModuleRef>();
        var frameIndices = new HashSet<uint>();
        ViewPreferences? vp = null; GPSData? gps = null;
        long off = 0;
        while (off + 32 <= data.Length &&
               data[off] == (byte)'L' && data[off + 1] == (byte)'E' && data[off + 2] == (byte)'L' && data[off + 3] == (byte)'R')
        {
            var span = data.AsSpan((int)off);
            ulong blockLen = BinaryPrimitives.ReadUInt64LittleEndian(span[4..]);
            ulong msgOff = BinaryPrimitives.ReadUInt64LittleEndian(span[12..]);
            uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            byte type = data[off + 24];
            if (type == 0) // LightHeader (possibly partial)
            {
                var part = LightHeader.Parser.ParseFrom(data.AsSpan((int)(off + (long)msgOff), (int)msgLen));
                header.MergeFrom(part);
                foreach (var m in part.Modules)
                {
                    string id = m.Id.ToString();
                    if (!perFrame.TryGetValue(id, out var byFrame)) perFrame[id] = byFrame = new SortedDictionary<uint, ModuleRef>();
                    byFrame[m.FrameIndex] = new ModuleRef(m, off);   // last block defining (module, frame) wins (matches Python for the single-frame case)
                    blocks.Add(new ModuleRef(m, off));
                    frameIndices.Add(m.FrameIndex);
                }
            }
            else if (type == 1) // standalone ViewPreferences
            {
                var part = ViewPreferences.Parser.ParseFrom(data.AsSpan((int)(off + (long)msgOff), (int)msgLen));
                if (vp is null) vp = part; else vp.MergeFrom(part);
            }
            else if (type == 2) // standalone GPSData
            {
                var part = GPSData.Parser.ParseFrom(data.AsSpan((int)(off + (long)msgOff), (int)msgLen));
                if (gps is null) gps = part; else gps.MergeFrom(part);
            }
            if (blockLen == 0) break;
            off += (long)blockLen;
        }
        var mods = new Dictionary<string, ModuleRef>();
        var frames = new Dictionary<string, IReadOnlyList<ModuleRef>>();
        foreach (var (id, byFrame) in perFrame)
        {
            var list = new List<ModuleRef>(byFrame.Count);
            foreach (var kv in byFrame) list.Add(kv.Value);          // ordered by frame_index
            frames[id] = list;
            // `FUN_180110190(hdr, out, 0, camId)` — frame 0; a module without one keeps the last block seen, as before
            mods[id] = byFrame.TryGetValue(0u, out var f0) ? f0 : list[^1];
        }
        return new LriFile(data, header, mods, frames, blocks, vp, gps, Math.Max(frameIndices.Count, 1));
    }

    /// <summary>The reference module (`LightHeader.image_reference_camera`), e.g. "A1".</summary>
    public string ReferenceModule => Header.ImageReferenceCamera.ToString();

    /// <summary>
    /// Lumen's exposure ratio (SoT §3.7, `FUN_180126860`): (image_gain · image_integration_time_ns) /
    /// (reference module sensor_analog_gain · sensor_exposure); 1.0 when the type-1 block lacks the two fields.
    /// <c>log2</c> of it is the DNG BaselineExposure and the `tone_mapping.ev_offset`.
    /// </summary>
    public float LumenExposureRatio
    {
        get
        {
            var vp = ViewPreferencesBlock;
            if (vp is null || !vp.HasImageGain || !vp.HasImageIntegrationTimeNs) return 1f;
            var refm = Modules[ReferenceModule].Module;
            float capTime = (float)refm.SensorExposure, capGain = refm.SensorAnalogGain;   // (float)(u64), float — as cp.dll
            return (vp.ImageGain * (float)vp.ImageIntegrationTimeNs) / (capGain * capTime);
        }
    }

    /// <summary>`FUN_180126860(CapturedImage)`: the same ViewPreferences (image_gain·integration_time) over THIS module's own gain·exposure — equals
    /// <see cref="LumenExposureRatio"/> for the reference module; ≈1.012 for the L16 B-cams at gain 7.25 (verified: the tele level-1 grain rule
    /// `max(0.5, rsqrt(ratio))` yields 0.994, not 0.703 — cp.dll tele-ISP reference run t0, 2026-08-27).</summary>
    public float ExposureRatioOf(CameraModule m)
    {
        var vp = ViewPreferencesBlock;
        if (vp is null || !vp.HasImageGain || !vp.HasImageIntegrationTimeNs) return 1f;
        return (vp.ImageGain * (float)vp.ImageIntegrationTimeNs) / (m.SensorAnalogGain * (float)m.SensorExposure);
    }

    /// <summary>`tone_mapping.ev_offset` / BaselineExposure = log2f(<see cref="LumenExposureRatio"/>) (SoT §3.7).</summary>
    public float LumenEvOffset => MathF.Log2(LumenExposureRatio);

    /// <summary>
    /// Lumen's AsShot neutral (SoT §3.4, `1802095e0` L~300–330): n = (1/r, 1/g, 1/b) from the header's awb_gains
    /// (Vec3 r, g=g_r, b), divided by max(n) so the largest component is 1.0. (1,1,1) when absent.
    /// </summary>
    private static float RcpNr(float x)
    {
        float r = System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.ReciprocalScalar(System.Runtime.Intrinsics.Vector128.CreateScalar(x)).GetElement(0) : 1f / x;
        return ((1f - x * r) * r + r) * 1f;
    }

    public float[] LumenNeutral => AsShotNeutral(Header, ViewPreferencesBlock);

    public static float[] AsShotNeutral(LightHeader header, ViewPreferences? vpBlock = null)
    {
        {
            var awb = header.ViewPreferences?.AwbGains ?? vpBlock?.AwbGains;
            if (awb is null || awb.R <= 0f || awb.GR <= 0f || awb.B <= 0f) return new[] { 1f, 1f, 1f };
            // FUN_1802095e0 L342-357 (asm-exact): (1/r, 1/g) by rcpps + one Newton step, 1/b by divss, divide by the max
            float nr = RcpNr(awb.R), ng = RcpNr(awb.GR), nb = 1f / awb.B;
            float mx = nr; if (mx <= ng) mx = ng; if (mx <= nb) mx = nb;
            float inv = 1f / mx;
            return new[] { nr * inv, ng * inv, inv * nb };
        }
    }

    /// <summary>Sensor black/white levels from the merged header (AR1335: 42 / 1023), with fallbacks.</summary>
    public (float Black, float White) SensorLevels =>
        Header.SensorData.Count > 0
            ? (Header.SensorData[0].Data.BlackLevel, Header.SensorData[0].Data.WhiteLevel)
            : (42f, 1023f);

    /// <summary>
    /// Unpack a module's raw frame into a row-major ushort[H*W]. Two encodings exist and cp.dll dispatches between them
    /// in `lt::CaptureStack::CaptureStack::lambda_0` (`FUN_180118bb0`): `RAW_PACKED_10BPP` → `FUN_180127670`, the 4 px
    /// per 5 bytes unpack below; `RAW_BAYER_JPEG` → `FUN_180128550`, four (or one) baseline JPEG planes plus a
    /// dequantization LUT (<see cref="BayerJpegSurface"/>). Everything else is *"Unsupported sensor data encoding!"*.
    /// </summary>
    public ushort[] Frame(in ModuleRef mref, out int width, out int height)
    {
        CameraModule.Types.Surface s = mref.Module.SensorDataSurface;
        width = s.Size.X;
        height = s.Size.Y;
        if (s.Format == CameraModule.Types.Surface.Types.FormatType.RawBayerJpeg)
            return BayerJpegSurface.Decode(Data, mref.BlockBase + (long)s.DataOffset, width, height);
        if (s.Format != CameraModule.Types.Surface.Types.FormatType.RawPacked10Bpp)
            throw new NotSupportedException($"module {mref.Module.Id}: surface format {s.Format} is neither RAW_PACKED_10BPP nor RAW_BAYER_JPEG");
        int stride = (int)s.RowStride;
        long dataStart = mref.BlockBase + (long)s.DataOffset;
        int groupsPerRow = width / 4;
        var pix = new ushort[(long)height * width];
        ReadOnlySpan<byte> b = Data;
        for (int y = 0; y < height; y++)
        {
            int rowBase = (int)(dataStart + (long)y * stride);
            int px = y * width;
            for (int g = 0; g < groupsPerRow; g++)
            {
                int o = rowBase + g * 5;
                int b0 = b[o], b1 = b[o + 1], b2 = b[o + 2], b3 = b[o + 3], b4 = b[o + 4];
                pix[px + 0] = (ushort)(b0 | ((b1 & 0x3) << 8));
                pix[px + 1] = (ushort)((b1 >> 2) | ((b2 & 0xf) << 6));
                pix[px + 2] = (ushort)((b2 >> 4) | ((b3 & 0x3f) << 4));
                pix[px + 3] = (ushort)((b3 >> 6) | (b4 << 2));
                px += 4;
            }
        }
        return pix;
    }
}
