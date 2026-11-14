using System.Buffers.Binary;

namespace Lux.Engine.Pipeline.Dof.Edit;

/// <summary>
/// One record of a Lumen session file (`.lris`). Every state component writes
/// <code>
///   +0x00 u32 id          +0x04 u64 total bytes (header included)        +0x0c u64 offset of the protobuf message in the record
///   +0x14 u32 protobuf bytes        +0x18 8 bytes not written (stack residue)        +0x20 binary payload … protobuf message
/// </code>
/// and the file is the records back to back in the order RendererState, Transform, Stereo, DepthEditor, MetaData;
/// the loader walks them by <c>total</c> and skips ids it has no component for.
/// </summary>
public sealed class LumenSessionRecord
{
    public const uint RendererStateId = 0x12345678, TransformId = 0x12340011, StereoId = 0x51e8e000, DepthEditorId = 0xabcd, MetaDataId = 0xa1b2c3;
    public const int HeaderBytes = 0x20;

    public uint Id { get; init; }
    /// <summary>Offset of the record in the file.</summary>
    public long Offset { get; init; }
    /// <summary>Offset and size of the protobuf message inside the record.</summary>
    public long ProtoOffset { get; init; }
    public int ProtoLength { get; init; }
    /// <summary>The whole record, header included, exactly as stored.</summary>
    public ReadOnlyMemory<byte> Bytes { get; init; }
    public int Length => Bytes.Length;
    /// <summary>The binary payload between the header and the protobuf message.</summary>
    public ReadOnlyMemory<byte> Payload => Bytes[HeaderBytes..(int)ProtoOffset];
    public ReadOnlyMemory<byte> Proto => Bytes.Slice((int)ProtoOffset, ProtoLength);
    public string Name => Id switch
    {
        RendererStateId => "RendererState", TransformId => "Transform", StereoId => "Stereo", DepthEditorId => "DepthEditor", MetaDataId => "MetaData",
        _ => $"0x{Id:x}",
    };
}

/// <summary>`ltpb::DepthEditorState.DataType`: what a saved tile carries.</summary>
public enum LumenTileData { DepthOnly = 1, OcclusionOnly = 2, DepthAndOcc = 3 }

/// <summary>One tile of a saved depth-editor state, decoded: the depth (types 1, 3) and the layer pair — background depth and layer mask —
/// (types 2, 3). Depth images are ".dpc" streams (<see cref="DpcCodec"/>) or, when the stream does not start with "Dh", raw floats; the
/// mask is raw bytes, one per pixel.</summary>
public sealed class LumenDepthTile
{
    public int Tx { get; init; }
    public int Ty { get; init; }
    public LumenTileData Type { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public float[]? Depth { get; init; }
    public float[]? Background { get; init; }
    public byte[]? Mask { get; init; }
    /// <summary>The tile's bytes in the record payload (offset from the start of the record, length).</summary>
    public (int Offset, int Length) Blob { get; init; }
    /// <summary>The stored depth stream alone (offset from the start of the record, length); (0, 0) for a layers-only tile.</summary>
    public (int Offset, int Length) DepthBlob { get; init; }
    /// <summary>The stored background-depth stream alone; (0, 0) when the tile has no layers.</summary>
    public (int Offset, int Length) BackgroundBlob { get; init; }
}

/// <summary>
/// The DepthEditor record (id 0xabcd): `ltpb::DepthEditorState` { depth_size, tile_size,
/// tiles { index, dtype } } plus, in tile order, each tile's images. The record exists only when at least one tile had a version at or
/// below the undo cursor when the session was saved — its presence is what `CIAPI::StateFileEditor::hasDepthEdits` reports.
/// Tiles are the flattened state at the cursor; the undo history is not stored.
/// </summary>
public sealed class LumenDepthEditorState
{
    public (int W, int H) DepthSize { get; init; }
    public (int W, int H) TileSize { get; init; }
    public IReadOnlyList<LumenDepthTile> Tiles { get; init; } = Array.Empty<LumenDepthTile>();
    /// <summary>The record as stored (kept verbatim by an import).</summary>
    public LumenSessionRecord Record { get; init; } = null!;

    /// <summary>Tile counts and the rect of a tile for a level of <paramref name="size"/> cut into <paramref name="tile"/>-sized tiles —
    /// the grid rule of the depth editor (see <see cref="DepthEditor.TileRect"/>).</summary>
    public static (int Nx, int Ny) Grid((int W, int H) size, (int W, int H) tile)
    {
        int nx = size.W / tile.W; if (size.W % tile.W != 0) nx++;
        int ny = size.H / tile.H; if (size.H % tile.H != 0) ny++;
        if (nx > 1 && size.W < tile.W * nx) nx--;
        if (ny > 1 && size.H < tile.H * ny) ny--;
        return (nx, ny);
    }

    public static RectI TileRect((int W, int H) size, (int W, int H) tile, int tx, int ty)
    {
        var (nx, ny) = Grid(size, tile);
        int x0 = tx * tile.W, y0 = ty * tile.H, w = tile.W, h = tile.H;
        if (tx == nx - 1) w = Math.Min(x0 + 2 * tile.W, size.W) - x0;
        if (ty == ny - 1) h = Math.Min(y0 + 2 * tile.H, size.H) - y0;
        return new RectI(x0, y0, x0 + w, y0 + h);
    }

    /// <summary>
    /// Parse the record the way Lumen's reader does: the message first (both sizes and at least one tile required), then the images in
    /// tile order from +0x20 — type 1: depth; type 3: depth, background depth, mask (w·h bytes); type 2: background depth, mask.
    /// </summary>
    public static LumenDepthEditorState Parse(LumenSessionRecord rec)
    {
        if (rec.Id != LumenSessionRecord.DepthEditorId) throw new InvalidDataException("Invalid magic ID in renderer record in state file!");
        var msg = Ltpb.DepthEditorState.Parser.ParseFrom(rec.Proto.Span);
        if (msg.DepthSize == null || msg.TileSize == null || msg.Tiles.Count < 1) throw new InvalidDataException("the depth-editor record holds no tiles");
        (int W, int H) size = (msg.DepthSize.X, msg.DepthSize.Y), tile = (msg.TileSize.X, msg.TileSize.Y);
        if (size.W <= 0 || size.H <= 0 || tile.W <= 0 || tile.H <= 0) throw new InvalidDataException("the depth-editor record has invalid sizes");
        var (nx, ny) = Grid(size, tile);
        var src = rec.Bytes.Span; int end = (int)rec.ProtoOffset, p = LumenSessionRecord.HeaderBytes;
        var tiles = new List<LumenDepthTile>(msg.Tiles.Count);
        foreach (var t in msg.Tiles)
        {
            int tx = t.Index?.X ?? 0, ty = t.Index?.Y ?? 0;
            if (tx < 0 || ty < 0 || tx >= nx || ty >= ny) throw new InvalidDataException($"tile index ({tx}, {ty}) lies outside the {nx}x{ny} grid");
            var r = TileRect(size, tile, tx, ty); int w = r.Width, h = r.Height, start = p;
            float[]? depth = null, bg = null; byte[]? mask = null; (int, int) db = (0, 0), bb = (0, 0);
            var type = (LumenTileData)(int)t.Dtype;
            if (type is LumenTileData.DepthOnly or LumenTileData.DepthAndOcc)
            {
                int s = p; depth = Image(src[..end], ref p, w, h); db = (s, p - s);
            }
            if (type is LumenTileData.DepthAndOcc or LumenTileData.OcclusionOnly)
            {
                // background depth and layer mask
                int s = p; bg = Image(src[..end], ref p, w, h); bb = (s, p - s);
                if (end - p < w * h) throw new InvalidDataException("the depth-editor record is truncated (layer mask)");
                mask = src.Slice(p, w * h).ToArray(); p += w * h;
            }
            tiles.Add(new LumenDepthTile { Tx = tx, Ty = ty, Type = type, W = w, H = h, Depth = depth, Background = bg, Mask = mask,
                                           Blob = (start, p - start), DepthBlob = db, BackgroundBlob = bb });
        }
        return new LumenDepthEditorState { DepthSize = size, TileSize = tile, Tiles = tiles, Record = rec };
    }

    /// <summary>A float image of the tile's size — a ".dpc" stream when the next bytes are "Dh", else w·h raw floats.</summary>
    static float[] Image(ReadOnlySpan<byte> src, ref int p, int w, int h)
    {
        if (DpcCodec.IsDpc(src, p))
        {
            var d = DpcCodec.Decode(src, ref p, out int dw, out int dh);
            if (dw != w || dh != h) throw new InvalidDataException($"a stored tile image is {dw}x{dh}, its tile {w}x{h}");
            return d;
        }
        long n = (long)w * h * 4;
        if (src.Length - p < n) throw new InvalidDataException("the depth-editor record is truncated (raw depth)");
        var o = new float[w * h];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src.Slice(p, (int)n)).CopyTo(o);
        p += (int)n;
        return o;
    }
}

/// <summary>`CIAPI::StateFileEditor::DepthType`: how the Stereo record stores the capture's depth map.</summary>
public enum LumenDepthType { None = 0, Legacy = 1, Dpc = 2 }

/// <summary>
/// The Stereo record (id 0x51e8e000; `lt::StereoAsyncAPI`): the stereo stage's saved
/// result — calibration points, the refined calibration and, when the stage had finished, <b>the capture's own depth map</b> (the
/// stereo depth, 4160×3120 on the L16, before any depth edit) as a ".dpc" stream at <c>depth_offset</c> of the payload. It is written
/// for every capture whose depth was computed, edited or not; a reload takes the depth from here instead of recomputing it.
/// </summary>
public sealed class LumenStereoState
{
    public LumenSessionRecord Record { get; init; } = null!;
    public Ltpb.Stereo Message { get; init; } = null!;
    public LumenDepthType DepthType { get; init; }
    /// <summary>Offset of the depth stream from the start of the record; −1 when the record has none.</summary>
    public int DepthOffset { get; init; } = -1;
    /// <summary>Dims of the stored depth (0, 0 when there is none or it is not a ".dpc" stream).</summary>
    public (int W, int H) DepthSize { get; init; }

    public static LumenStereoState Parse(LumenSessionRecord rec)
    {
        var msg = Ltpb.Stereo.Parser.ParseFrom(rec.Proto.Span);
        int off = -1; var type = LumenDepthType.None; (int, int) size = (0, 0);
        if (msg.HasDepthOffset)
        {
            long o = LumenSessionRecord.HeaderBytes + (long)msg.DepthOffset;
            if (o < 0 || o + 2 > rec.ProtoOffset) throw new InvalidDataException("the stereo record's depth offset lies outside its payload");
            off = (int)o; var src = rec.Bytes.Span;
            if (DpcCodec.IsLegacyDepth(src, off)) type = LumenDepthType.Legacy;
            else if (DpcCodec.IsDpc(src, off)) { type = LumenDepthType.Dpc; size = DpcCodec.ReadSize(src, off); }
        }
        return new LumenStereoState { Record = rec, Message = msg, DepthType = type, DepthOffset = off, DepthSize = size };
    }

    /// <summary>The refined calibration the record stores (`ltpb::RefinedGeomCalib`: module id, K, R, t as float32 — what Lumen puts
    /// back into each module's CURRENT slot), by module id.</summary>
    public IReadOnlyDictionary<int, (float[] K, float[] R, float[] T)> RefinedCalibration() => Calibration(Message);

    /// <summary>The same from a `ltpb::Stereo` message alone (an import keeps only the message).</summary>
    public static IReadOnlyDictionary<int, (float[] K, float[] R, float[] T)> Calibration(Ltpb.Stereo m)
    {
        static float[] M(Ltpb.Matrix3x3F x) => new[] { x.X00, x.X01, x.X02, x.X10, x.X11, x.X12, x.X20, x.X21, x.X22 };
        var o = new Dictionary<int, (float[], float[], float[])>();
        foreach (var c in m.Calibration) o[(int)c.ModuleId] = (M(c.KMat), M(c.RMat), new[] { c.TVec.X, c.TVec.Y, c.TVec.Z });
        return o;
    }

    /// <summary>The stored depth map, decoded (<see cref="LumenDepthType.Dpc"/> only).</summary>
    public float[] DecodeDepth(out int width, out int height)
    {
        if (DepthType != LumenDepthType.Dpc) throw new InvalidOperationException("the session holds no depth map in the .dpc format");
        int p = DepthOffset;
        return DpcCodec.Decode(Record.Bytes.Span[..(int)Record.ProtoOffset], ref p, out width, out height);
    }
}

/// <summary>A parsed `.lris` file: its records verbatim, the two that matter for depth — the Stereo record (Lumen's own depth map)
/// and the DepthEditor record (the depth edits) — and, on demand, the settings records (<see cref="Settings"/>).</summary>
public sealed class LumenSession
{
    public IReadOnlyList<LumenSessionRecord> Records { get; init; } = Array.Empty<LumenSessionRecord>();
    /// <summary>The DepthEditor record, decoded; null when the session holds no depth edits.</summary>
    public LumenDepthEditorState? DepthEditor { get; init; }
    public LumenStereoState? Stereo { get; init; }
    /// <summary>`CIAPI::StateFileEditor::hasDepthEdits`: the file contains a DepthEditor record (id 0xabcd).</summary>
    public bool HasDepthEdits => Find(LumenSessionRecord.DepthEditorId) != null;
    public LumenSessionRecord? Find(uint id) => Records.FirstOrDefault(r => r.Id == id);

    LumenSessionSettings? _settings;
    /// <summary>The settings records decoded (<see cref="LumenSessionSettings"/>): renderer settings, transform, rating, image UUID. Read on
    /// first use; throws <see cref="InvalidDataException"/> where Lumen's load of the file would fail.</summary>
    public LumenSessionSettings Settings => _settings ??= LumenSessionSettings.Read(this);
}

public static class LumenSessionReader
{
    public static LumenSession Read(string path, bool decode = true) => Read(File.ReadAllBytes(path), decode);

    /// <summary>
    /// Split the file into records (read the header at the current offset, advance by its total) and decode the Stereo
    /// header and the DepthEditor record. With <paramref name="decode"/> false only the records are listed
    /// (<see cref="LumenSession.HasDepthEdits"/> needs no more).
    /// </summary>
    public static LumenSession Read(byte[] bytes, bool decode = true)
    {
        var recs = new List<LumenSessionRecord>(); long pos = 0;
        while (pos != bytes.LongLength)
        {
            if (bytes.LongLength - pos < LumenSessionRecord.HeaderBytes) throw new InvalidDataException($"truncated record header at offset {pos}");
            var h = bytes.AsSpan((int)pos);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            long total = BinaryPrimitives.ReadInt64LittleEndian(h[4..]), protoOff = BinaryPrimitives.ReadInt64LittleEndian(h[12..]);
            int protoLen = BinaryPrimitives.ReadInt32LittleEndian(h[20..]);
            if (total < LumenSessionRecord.HeaderBytes || total > bytes.LongLength - pos)
                throw new InvalidDataException($"record 0x{id:x} at offset {pos} claims {total} bytes, {bytes.LongLength - pos} remain");
            if (protoOff < LumenSessionRecord.HeaderBytes || protoLen < 0 || protoOff + protoLen > total)
                throw new InvalidDataException($"record 0x{id:x} at offset {pos} has an invalid message range");
            recs.Add(new LumenSessionRecord { Id = id, Offset = pos, ProtoOffset = protoOff, ProtoLength = protoLen,
                                              Bytes = new ReadOnlyMemory<byte>(bytes, (int)pos, (int)total) });
            pos += total;
        }
        LumenDepthEditorState? ed = null; LumenStereoState? st = null;
        if (decode)
        {
            var e = recs.FirstOrDefault(r => r.Id == LumenSessionRecord.DepthEditorId);
            if (e != null) ed = LumenDepthEditorState.Parse(e);
            var s = recs.FirstOrDefault(r => r.Id == LumenSessionRecord.StereoId);
            if (s != null) st = LumenStereoState.Parse(s);
        }
        return new LumenSession { Records = recs, DepthEditor = ed, Stereo = st };
    }
}
