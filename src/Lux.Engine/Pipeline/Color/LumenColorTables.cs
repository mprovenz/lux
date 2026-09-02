using Ltpb;

namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// Constant tables from cp.dll used by the colour-profile fit (SoT §9), dumped bit-exact from `.rdata`
/// (`scratch/tools/huesat_fit.py` reads the same addresses). All are float32 in the binary.
/// </summary>
public static class LumenColorTables
{
    /// <summary>Built-in ColorChecker reference (24 × XYZ, D50-relative, white patch 19 Y = 0.912874); static-init
    /// source `0x18069fdc0` → `DAT_1808320e0`.</summary>
    public static readonly float[] ReferenceChartXyz =
    {
        0.118002f, 0.1033662f, 0.05151056f, 0.3941671f, 0.3525108f, 0.193914f, 0.1697489f, 0.1847199f, 0.2604046f,
        0.1097356f, 0.1334858f, 0.05336417f, 0.2438264f, 0.2323455f, 0.3314437f, 0.304897f, 0.417442f, 0.3453007f,
        0.4046375f, 0.3117067f, 0.04849074f, 0.1236424f, 0.1139785f, 0.2912118f, 0.3009016f, 0.1979009f, 0.102105f,
        0.0837238f, 0.06437369f, 0.1037335f, 0.3538522f, 0.4434718f, 0.08990558f, 0.4882031f, 0.4357746f, 0.0600808f,
        0.06963614f, 0.05788863f, 0.2139047f, 0.1498943f, 0.2307068f, 0.07780055f, 0.2194251f, 0.1267768f, 0.03820157f,
        0.6029922f, 0.6080151f, 0.07381216f, 0.3098463f, 0.200638f, 0.2314513f, 0.1347687f, 0.1903196f, 0.3016372f,
        0.8775231f, 0.912874f, 0.7255891f, 0.5647668f, 0.588492f, 0.4833206f, 0.3450554f, 0.3595135f, 0.2967404f,
        0.1831045f, 0.1912013f, 0.1583591f, 0.08549204f, 0.08932398f, 0.07493845f, 0.03083086f, 0.03196162f, 0.0268612f,
    };

    /// <summary>Illuminant chromaticity by Lumen's internal illuminant enum (`FUN_1800ce600`, `DAT_180687bc0`/`c00`):
    /// 2 A, 3 B, 4 C, 5 D50, 6 D55, 7 D65, 8 D75, 9 E, 10 F2, 11 F7, 12 F11 (0/1 unused).</summary>
    public static readonly float[] IlluminantX =
        { 0f, 0f, 0.44757268f, 0.3484831f, 0.3100605f, 0.34566918f, 0.33242422f, 0.31272662f, 0.2990208f, 0.33333334f, 0.3720698f, 0.31285304f, 0.38054064f };
    public static readonly float[] IlluminantY =
        { 0f, 0f, 0.40743986f, 0.3517473f, 0.31614956f, 0.3584962f, 0.3474261f, 0.32902312f, 0.31485155f, 0.33333334f, 0.37512332f, 0.32917693f, 0.37691474f };

    public const int IllumA = 2, IllumD50 = 5, IllumD65 = 7, IllumD75 = 8, IllumF2 = 10, IllumF7 = 11, IllumF11 = 12;

    /// <summary>`color_calibration.proto` IlluminantType → internal enum (SoT §9.1; TL84 is treated as F11).</summary>
    public static int InternalIlluminant(ColorCalibration.Types.IlluminantType t) => t switch
    {
        ColorCalibration.Types.IlluminantType.A => IllumA,
        ColorCalibration.Types.IlluminantType.D50 => IllumD50,
        ColorCalibration.Types.IlluminantType.D65 => IllumD65,
        ColorCalibration.Types.IlluminantType.D75 => IllumD75,
        ColorCalibration.Types.IlluminantType.F2 => IllumF2,
        ColorCalibration.Types.IlluminantType.F7 => IllumF7,
        ColorCalibration.Types.IlluminantType.F11 => IllumF11,
        ColorCalibration.Types.IlluminantType.Tl84 => IllumF11,
        _ => throw new NotSupportedException($"illuminant {t} has no Lumen chromaticity"),
    };

    /// <summary>Internal illuminant enum → EXIF LightSource code written as CalibrationIlluminant1/2 (`DAT_18069f860`).</summary>
    public static int ExifLightSource(int internalIllum) => internalIllum switch
    {
        IllumA => 17, IllumD50 => 23, IllumD65 => 21, IllumD75 => 22, IllumF2 => 14, IllumF7 => 13, IllumF11 => 15,
        _ => 0,
    };

    /// <summary>ProPhoto RGB → XYZ (D50 native), row-major (`DAT_1806879fc`; colour-space type 5 in `FUN_1800cef80`).</summary>
    public static readonly float[] ProPhotoToXyz =
        { 0.7976749f, 0.1351917f, 0.0313534f, 0.2880402f, 0.7118741f, 0.0000857f, 0f, 0f, 0.82521f };

    /// <summary>Robertson isotherm table (DNG-SDK `kTempTable`), rows of (mired, u, v, slope); static-init source
    /// `0x180687710` → `DAT_1808316e0` (float32 in Lumen).</summary>
    public static readonly float[] Robertson =
    {
        0f, 0.18006f, 0.26352f, -0.24341f, 10f, 0.18066f, 0.26589f, -0.25479f, 20f, 0.18133f, 0.26846f, -0.26876f,
        30f, 0.18208f, 0.27119f, -0.28539f, 40f, 0.18293f, 0.27407f, -0.3047f, 50f, 0.18388f, 0.27709f, -0.32675f,
        60f, 0.18494f, 0.28021f, -0.35156f, 70f, 0.18611f, 0.28342f, -0.37915f, 80f, 0.1874f, 0.28668f, -0.40955f,
        90f, 0.1888f, 0.28997f, -0.44278f, 100f, 0.19032f, 0.29326f, -0.47888f, 125f, 0.19462f, 0.30141f, -0.58204f,
        150f, 0.19962f, 0.30921f, -0.70471f, 175f, 0.20525f, 0.31647f, -0.84901f, 200f, 0.21142f, 0.32312f, -1.0182f,
        225f, 0.21807f, 0.32909f, -1.2168f, 250f, 0.22511f, 0.33439f, -1.4512f, 275f, 0.23247f, 0.33904f, -1.7298f,
        300f, 0.2401f, 0.34308f, -2.0637f, 325f, 0.24792f, 0.34655f, -2.4681f, 350f, 0.25591f, 0.34951f, -2.9641f,
        375f, 0.264f, 0.352f, -3.5814f, 400f, 0.27218f, 0.35407f, -4.3633f, 425f, 0.28039f, 0.35577f, -5.3762f,
        450f, 0.28863f, 0.35714f, -6.7262f, 475f, 0.29685f, 0.35823f, -8.5955f, 500f, 0.30505f, 0.35907f, -11.324f,
        525f, 0.3132f, 0.35968f, -15.628f, 550f, 0.32129f, 0.36011f, -23.325f, 575f, 0.32931f, 0.36038f, -40.77f,
        600f, 0.33724f, 0.36051f, -116.45f,
    };

    /// <summary>xy → (CCT, tint): DNG-SDK `dng_temperature::Set_xy_coord` (`FUN_1800d0ef0`), tint scale −3000
    /// (`DAT_180687544`).</summary>
    public static (double Cct, double Tint) XyToCct(double x, double y)
    {
        double den = 1.5 - x + 6.0 * y;
        double u = 2.0 * x / den, v = 3.0 * y / den;
        double lastDt = 0, lastDu = 0, lastDv = 0;
        for (int i = 1; i < 31; i++)
        {
            double du = 1.0, dv = Robertson[i * 4 + 3];
            double len = Math.Sqrt(du * du + dv * dv); du /= len; dv /= len;
            double uu = u - Robertson[i * 4 + 1], vv = v - Robertson[i * 4 + 2];
            double dt = -uu * dv + vv * du;
            if (dt <= 0.0 || i == 30)
            {
                if (dt > 0.0) dt = 0.0;
                dt = -dt;
                double f = i == 1 ? 0.0 : dt / (lastDt + dt);
                double mired = Robertson[(i - 1) * 4] * f + Robertson[i * 4] * (1.0 - f);
                double ud = Robertson[(i - 1) * 4 + 1] * f + Robertson[i * 4 + 1] * (1.0 - f);
                double vd = Robertson[(i - 1) * 4 + 2] * f + Robertson[i * 4 + 2] * (1.0 - f);
                uu = u - ud; vv = v - vd;
                du = du * (1.0 - f) + lastDu * f; dv = dv * (1.0 - f) + lastDv * f;
                len = Math.Sqrt(du * du + dv * dv); du /= len; dv /= len;
                double tint = (uu * du + vv * dv) * -3000.0;
                return (1.0e6 / mired, tint);
            }
            lastDt = dt; lastDu = du; lastDv = dv;
        }
        throw new InvalidOperationException("unreachable");
    }
}
