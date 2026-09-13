namespace Lux.Engine.Pipeline.Registration;

/// <summary>
/// Registration-stage dumps in the oracle's `regdump` layouts (`re/tools/lumen_oracle.c`, ORACLE_GEOM / ORACLE_BA hooks) so a Lux run
/// can be compared stage by stage with a Lumen run by `re/tools/reg_cmp.py`: `refpts`, per WIDE/TELE driver call the per-level
/// `MatchedPoint` vectors (0x2c records) and the full-res output (`drv&lt;i&gt;_match&lt;l&gt;` / `drv&lt;i&gt;_out`, `tdrv…`), the
/// FundamentalMatrixFilter in/out (`flt&lt;i&gt;_cam&lt;c&gt;_{pre,post}`), the triangulator points (`tri_{before,after,refined}_points`,
/// 20-byte {u,v,X,Y,Z}), the BA caller's slots before/after write-back (`bac&lt;i&gt;_{in,out}`), the post-acceptance slots
/// (`final&lt;i&gt;_slots`) and every optimiser calibration write (`calibwrite&lt;i&gt;_{before,after}`, 0x54 = {K, R, t} of the CURRENT slot).
/// Enabled by `LUX_DENSE_DUMP` (the prefix is shared with the dense-stereo dumps).
/// </summary>
public sealed class RegDump
{
    public readonly string Prefix;
    public int Drv, TDrv, Flt, Bac, Cw, Final;
    public RegDump(string prefix) { Prefix = prefix; }

    public void Bytes(string tag, byte[] b) => File.WriteAllBytes($"{Prefix}_{tag}.bin", b);

    public static byte[] Vec2((float X, float Y)[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++) { BitConverter.GetBytes(v[i].X).CopyTo(b, 8 * i); BitConverter.GetBytes(v[i].Y).CopyTo(b, 8 * i + 4); }
        return b;
    }

    /// <summary>`MatchedPoint` in Lumen's 0x2c layout {refIdx, mx, my, predX, predY, nmX, nmY, score, ratio, status, octave}.</summary>
    public static byte[] Matches(MatchedPoint[]? m)
    {
        if (m is null) return Array.Empty<byte>();
        var b = new byte[m.Length * 0x2c];
        for (int i = 0; i < m.Length; i++)
        {
            int o = i * 0x2c; var r = m[i];
            BitConverter.GetBytes(r.RefIdx).CopyTo(b, o); BitConverter.GetBytes(r.Mx).CopyTo(b, o + 4); BitConverter.GetBytes(r.My).CopyTo(b, o + 8);
            BitConverter.GetBytes(r.PredX).CopyTo(b, o + 12); BitConverter.GetBytes(r.PredY).CopyTo(b, o + 16); BitConverter.GetBytes(r.NmX).CopyTo(b, o + 20); BitConverter.GetBytes(r.NmY).CopyTo(b, o + 24);
            BitConverter.GetBytes(r.Score).CopyTo(b, o + 28); BitConverter.GetBytes(r.Ratio).CopyTo(b, o + 32); BitConverter.GetBytes(r.Status).CopyTo(b, o + 36); BitConverter.GetBytes(r.Octave).CopyTo(b, o + 40);
        }
        return b;
    }

    public static byte[] Tri(TriPoint[] p)
    {
        var b = new byte[p.Length * 20];
        for (int i = 0; i < p.Length; i++) { int o = 20 * i; BitConverter.GetBytes(p[i].U).CopyTo(b, o); BitConverter.GetBytes(p[i].V).CopyTo(b, o + 4); BitConverter.GetBytes(p[i].X).CopyTo(b, o + 8); BitConverter.GetBytes(p[i].Y).CopyTo(b, o + 12); BitConverter.GetBytes(p[i].Z).CopyTo(b, o + 16); }
        return b;
    }

    /// <summary>CalibData 0xa8 with K @0, t @0x24, R @0x30 (the rest zero: Lux's comparisons only read those).</summary>
    public static byte[] Calib(CalibDataFull c) { var b = new byte[0xa8]; Buffer.BlockCopy(c.K, 0, b, 0, 36); Buffer.BlockCopy(c.T, 0, b, 0x24, 12); Buffer.BlockCopy(c.R, 0, b, 0x30, 36); return b; }
    /// <summary>The 0x54-byte {K, t, R} slot image the oracle's `calibwrite` hook dumps (`module+0x12c`).</summary>
    public static byte[] Slot54(CalibDataFull c) { var b = new byte[0x54]; Buffer.BlockCopy(c.K, 0, b, 0, 36); Buffer.BlockCopy(c.R, 0, b, 0x24, 36); Buffer.BlockCopy(c.T, 0, b, 0x48, 12); return b; }
    /// <summary>The 0x88-byte view pose in Lumen's layout (`ViewPose.FromBytes` inverse).</summary>
    public static byte[] Pose(ViewPose p)
    {
        var b = new byte[0x88];
        for (int i = 0; i < 9; i++) BitConverter.GetBytes(p.P[i]).CopyTo(b, 4 * i);
        for (int i = 0; i < 3; i++) BitConverter.GetBytes(p.U[i]).CopyTo(b, 0x24 + 4 * i);
        BitConverter.GetBytes(p.Scale1.X).CopyTo(b, 0x30); BitConverter.GetBytes(p.Scale1.Y).CopyTo(b, 0x34); BitConverter.GetBytes(p.Shift1.X).CopyTo(b, 0x38); BitConverter.GetBytes(p.Shift1.Y).CopyTo(b, 0x3c);
        BitConverter.GetBytes(p.Scale2.X).CopyTo(b, 0x40); BitConverter.GetBytes(p.Scale2.Y).CopyTo(b, 0x44); BitConverter.GetBytes(p.Shift2.X).CopyTo(b, 0x48); BitConverter.GetBytes(p.Shift2.Y).CopyTo(b, 0x4c);
        for (int i = 0; i < 9; i++) BitConverter.GetBytes(p.Q[i]).CopyTo(b, 0x50 + 4 * i);
        BitConverter.GetBytes(p.Shift3.X).CopyTo(b, 0x74); BitConverter.GetBytes(p.Shift3.Y).CopyTo(b, 0x78); BitConverter.GetBytes(p.Scale3.X).CopyTo(b, 0x7c); BitConverter.GetBytes(p.Scale3.Y).CopyTo(b, 0x80);
        return b;
    }

    /// <summary>The oracle's `bac&lt;i&gt;_in` (with poses) / `bac&lt;i&gt;_out` and `final&lt;i&gt;_slots` (without) layout: n, then per camera
    /// `[int cam, pose 0x88,] int cam, calib 0xa8, int nd (= 0 here)`.</summary>
    public void Slots(string tag, IEnumerable<CdpCamera> cams, bool withPose)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        var list = cams.ToList(); w.Write(list.Count);
        foreach (var c in list)
        {
            if (withPose) { w.Write(c.Id); w.Write(Pose(c.Pose)); }
            w.Write(c.Id); w.Write(Calib(c.Slot)); w.Write(0);
        }
        w.Flush(); Bytes(tag, ms.ToArray());
    }
}
