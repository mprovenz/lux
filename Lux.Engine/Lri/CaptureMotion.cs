using Ltpb;

namespace Lux.Engine.Lri;

/// <summary>Why a capture has, or does not have, a level angle.</summary>
public enum LevelState
{
    /// <summary>The level angle is available.</summary>
    Available = 0,
    /// <summary>The capture has no accelerometer samples.</summary>
    NoMotionData = 1,
    /// <summary>The camera pointed up or down. Gravity then has almost no component in the picture plane.</summary>
    PointsUpOrDown = 2,
    /// <summary>The camera was held between landscape and portrait. The upright direction is then not certain.</summary>
    BetweenLandscapeAndPortrait = 3,
}

/// <summary>
/// The motion of the camera during a capture, from the motion sensor samples in the capture header.
/// <para>The camera writes 16 to 20 samples for each frame. The samples cover the readout of the reference module. An accelerometer sample
/// is an acceleration in g. A gyroscope sample is the rotation in degrees since the first sample of the capture, not a rate. Each sample
/// has the sensor row that the reference module read at that time.</para>
/// <para>All results use only addition, multiplication, division, square root and the functions of <see cref="Pipeline.WineCrt"/>. The
/// results are thus the same on every platform.</para>
/// </summary>
/// <param name="BlurPixels">The predicted motion blur at the picture center of the 70 mm modules, in full-size pixels. Null when the capture
/// has too few gyroscope samples or no 70 mm module.</param>
/// <param name="BlurIsExtrapolated">The samples do not cover the exposure. The blur is then the mean rotation rate of the sample window
/// multiplied by the exposure time.</param>
/// <param name="LevelDegrees">The rotation that makes the picture level, in degrees (see <see cref="LevelOf"/>). Null unless
/// <paramref name="Level"/> is <see cref="LevelState.Available"/>.</param>
public sealed record CaptureMotion(float? BlurPixels, bool BlurIsExtrapolated, float? LevelDegrees, LevelState Level)
{
    /// <summary>The raw sample rate of the motion sensor in samples per second. The camera keeps every second or every fourth sample.</summary>
    const double SampleRateHz = 1000.0;
    /// <summary>The rows of the sensor.</summary>
    const double SensorRows = 3120.0;
    /// <summary>The shortest line time of the sensor in seconds (4612 pixel clocks at 441.6 MHz).</summary>
    const double MinLineTime = 4612.0 / 441.6 * 1e-6;
    /// <summary>The focal length of the 70 mm modules in full-size pixels.</summary>
    const double FocalPixels70 = 8292.0;
    /// <summary>The mean gyroscope bias in degrees per second (x, y), measured on stacked captures. The camera does not remove the bias.</summary>
    const double BiasX = -0.5148104488814474, BiasY = 0.4229891192847471;
    /// <summary>The angle between the accelerometer and the sensor rows in degrees, measured against vertical lines in pictures.</summary>
    public const double MountingOffsetDegrees = -0.42;
    /// <summary>The level angle is not given when the upright direction is nearer than this to the border between landscape and portrait.</summary>
    public const double QuarterMarginDegrees = 15.0;
    const double DegreesPerRadian = 180.0 / 3.14159265358979323846;

    /// <summary>The motion results of a capture.</summary>
    /// <param name="heldDegrees">The quarter turn that shows the capture upright, in degrees clockwise (0, 90, 180, 270), or −1 when the
    /// camera recorded no usable gravity.</param>
    public static CaptureMotion Read(LriFile lri, int heldDegrees)
    {
        var (blur, extrapolated) = BlurOf(lri);
        var (level, state) = LevelOf(lri.Header, heldDegrees);
        return new CaptureMotion(blur, extrapolated, level, state);
    }

    /// <summary>
    /// The rotation that makes the picture level: the angle of gravity in the picture plane, relative to the held quarter turn, plus the
    /// mounting offset. A positive angle turns the picture content counter-clockwise. The angle uses the accelerometer samples of the
    /// first frame.
    /// </summary>
    public static (float? Degrees, LevelState State) LevelOf(LightHeader header, int heldDegrees)
    {
        var first = header.ImuData.Count > 0 ? header.ImuData[0].Accelerometer : null;
        if (first is null || first.Count == 0) return (null, LevelState.NoMotionData);
        if (heldDegrees < 0) return (null, LevelState.PointsUpOrDown);
        double gx = 0, gy = 0;
        foreach (var a in first) { gx += a.Data.X; gy += a.Data.Y; }
        gx /= first.Count; gy /= first.Count;
        // 0 = upright landscape, ±90 = portrait, 180 = upside down
        double roll = Pipeline.WineCrt.Atan2(gx, gy) * DegreesPerRadian;
        double quarter = heldDegrees switch { 90 => -90.0, 180 => 180.0, 270 => 90.0, _ => 0.0 };
        double rel = roll - quarter;
        while (rel >= 180.0) rel -= 360.0;
        while (rel < -180.0) rel += 360.0;
        if (45.0 - Math.Abs(rel) < QuarterMarginDegrees) return (null, LevelState.BetweenLandscapeAndPortrait);
        return ((float)(rel + MountingOffsetDegrees), LevelState.Available);
    }

    /// <summary>
    /// The predicted motion blur of the 70 mm modules at the picture center, in full-size pixels: the rotation of the camera about its x
    /// and y axes during the longest exposure of those modules, multiplied by their focal length.
    /// <para>The center row of the reference module is read at half of the readout. Its exposure ends at that time. When the samples cover
    /// the exposure, the rotation is the difference of the interpolated gyroscope angle at the two ends of the exposure. If not, the
    /// rotation is the mean rate of the sample window multiplied by the exposure time.</para>
    /// </summary>
    public static (float? Pixels, bool Extrapolated) BlurOf(LriFile lri)
    {
        var imu = lri.Header.ImuData;
        if (imu.Count == 0 || imu[0].Gyroscope.Count < 3) return (null, false);
        var g0 = imu[0].Gyroscope;
        int n = g0.Count;
        double row0 = g0[0].RowIdx, row1 = g0[n - 1].RowIdx;
        double step = (row1 - row0) / (n - 1);
        if (!(step > 0)) return (null, false);
        // The camera keeps every second sample when the sensor runs at the shortest line time (stacks, long exposures), else every fourth.
        double keep = step > 180.0 ? 2.0 : 4.0;
        double lineTime = keep / SampleRateHz / step;
        if (lineTime < MinLineTime * 0.97) { keep *= 2.0; lineTime = keep / SampleRateHz / step; }

        if (!lri.Modules.TryGetValue(lri.ReferenceModule, out var reference)) return (null, false);
        double exposure = 0;
        foreach (var (id, m) in lri.Modules)
            if (id.StartsWith('B')) exposure = Math.Max(exposure, m.Module.SensorExposure * 1e-9);
        if (!(exposure > 0)) return (null, false);

        // The frames of a stack follow each other with this period in rows. The gyroscope angle continues across the frames.
        double referenceLines = Math.Round(reference.Module.SensorExposure * 1e-9 / lineTime);
        double period = Math.Max(3138.0, referenceLines + 1.0);
        var t = new List<double>(); var x = new List<double>(); var y = new List<double>();
        for (int k = 0; k < imu.Count; k++)
            foreach (var s in imu[k].Gyroscope)
            {
                double time = (k * period + s.RowIdx) * lineTime;
                if (t.Count > 0 && !(time > t[^1])) continue;   // the times must increase
                t.Add(time); x.Add(s.Data.X); y.Add(s.Data.Y);
            }
        if (t.Count < 3) return (null, false);
        // remove the mean bias
        for (int i = 0; i < t.Count; i++) { x[i] -= (t[i] - t[0]) * BiasX; y[i] -= (t[i] - t[0]) * BiasY; }

        double end = SensorRows / 2.0 * lineTime, start = end - exposure;
        double dx, dy;
        bool covered = start >= t[0] && end <= t[^1];
        if (covered)
        {
            dx = At(t, x, end) - At(t, x, start);
            dy = At(t, y, end) - At(t, y, start);
        }
        else
        {
            double window = t[^1] - t[0];
            dx = (x[^1] - x[0]) / window * exposure;
            dy = (y[^1] - y[0]) / window * exposure;
        }
        double degrees = Math.Sqrt(dx * dx + dy * dy);
        return ((float)(degrees / DegreesPerRadian * FocalPixels70), !covered);
    }

    /// <summary>The linear interpolation of the values <paramref name="v"/> at the time <paramref name="at"/>.</summary>
    static double At(List<double> t, List<double> v, double at)
    {
        if (at <= t[0]) return v[0];
        for (int i = 1; i < t.Count; i++)
            if (at <= t[i]) return v[i - 1] + (v[i] - v[i - 1]) * ((at - t[i - 1]) / (t[i] - t[i - 1]));
        return v[^1];
    }
}
