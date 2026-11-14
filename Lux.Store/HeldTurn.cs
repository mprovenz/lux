using Lux.Engine.Lri;
using Lux.Engine.Pipeline.Export;

namespace Lux.Store;

/// <summary>
/// How the camera was held, and the photo transform that turns such a photo upright the way Lumen's own rotate does (the
/// app's exports follow the view shown as held; <c>lux-light convert --upright</c> applies the same turn). The L16 writes no orientation and
/// Lumen never turns a photo by itself; the held turn comes from the accelerometer (mean gravity vector: +y upright, −x → 90° clockwise,
/// +x → 270°, −y → 180°).
/// </summary>
public static class HeldTurn
{
    /// <summary>Degrees clockwise that show the capture upright (0, 90, 180, 270), or −1 when the camera recorded no usable gravity.</summary>
    public static int Degrees(LriFile lri) => Degrees(lri.Header);

    /// <summary>Applies the same rule to a header. The header can be the merged header or the first block only
    /// (<see cref="LriFile.ReadFirstBlockHeader"/>). The L16 writes the accelerometer into the first block only, so both give the same result.</summary>
    public static int Degrees(Ltpb.LightHeader header)
    {
        var acc = header.ImuData.SelectMany(m => m.Accelerometer).Select(a => a.Data).ToList();
        if (acc.Count == 0) return -1;
        double gx = acc.Average(a => a.X), gy = acc.Average(a => a.Y);
        // gravity along +y = held upright (landscape); along −x the top of the scene is the image's left: turn 90° clockwise to show it upright
        if (Math.Max(Math.Abs(gx), Math.Abs(gy)) <= 0.4) return -1;
        return Math.Abs(gy) >= Math.Abs(gx) ? (gy > 0 ? 0 : 180) : (gx < 0 ? 90 : 270);
    }

    /// <summary>
    /// The transform Lumen's rotate right (90), twice (180) or rotate left (270) makes of an untouched photo of aspect
    /// <paramref name="aspectW"/>:<paramref name="aspectH"/> — what the app saves for "Rotate as the camera was held" — bit for bit (Lumen's
    /// `GeometryEdits` on its `TransformImpl`; its crop goes through doubles, so x0 is 0x3e000001, not 0.125). Known for 4:3, the aspect
    /// of every L16 capture (proved equal to the app's own computation by `UprightTransformTests`); null for 0° or another aspect.
    /// </summary>
    public static ExportTransform? Transform(int aspectW, int aspectH, int degrees)
    {
        if (aspectW != 4 || aspectH != 3) return null;
        static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));
        static ExportTransform T(uint[] m, uint[] c) => new()
        {
            Matrix = m.Select(F).ToArray(), AspectW = 4, AspectH = 3, CropX0 = F(c[0]), CropY0 = F(c[1]), CropX1 = F(c[2]), CropY1 = F(c[3]),
        };
        return (((degrees % 360) + 360) % 360) switch
        {
            90 => T(R90M, R90C),
            180 => T(R180M, R180C),
            270 => T(R270M, R270C),
            _ => null,
        };
    }

    // the app's computation, as bit patterns (matrix m0…m8 output → capture; crop x0, y0, x1, y1)
    static readonly uint[] R90M = { 0x00000000, 0xbf800000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x3f000000, 0x40600000, 0x3f800000 };
    static readonly uint[] R90C = { 0x3e000001, 0xbe2aaaac, 0x3f600000, 0x3f955556 };
    static readonly uint[] R180M = { 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x40800000, 0x40400000, 0x3f800000 };
    static readonly uint[] R180C = { 0x00000000, 0x00000000, 0x3f800000, 0x3f800000 };
    static readonly uint[] R270M = { 0x00000000, 0x3f800000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x40600000, 0xbf000000, 0x3f800000 };
    static readonly uint[] R270C = { 0x3e000001, 0xbe2aaaac, 0x3f600000, 0x3f955556 };
}
