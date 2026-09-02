using Ltpb;

namespace Lux.Engine.Pipeline.Isp;

/// <summary>
/// `lt::Sensor` noise tables (ctor `FUN_18011eb90` colour / `FUN_18011fda0` mono, per-channel builder `FUN_18011f5b0`,
/// table maths `FUN_18011e550`, ISO lookup `FUN_180120cb0`, channel selection `FUN_180120f50`): for every
/// `SensorCharacterization.vst_model` (gain = ISO key) and channel, a σ table indexed by raw DN, built from the
/// affine variance model `var(x) = a·x + b` of the normalised signal x = (DN + 0.5)/N, N = white + 1, with a
/// clipped-Gaussian correction near zero (`σ · ½(1 + tanh(P(x/σ)))`), a linear extrapolation below
/// `(int)(black · cliff_slope)` and a final ×white to DN units. The companion table A = ((DN+0.5)/N)/σ_norm (SNR) is
/// kept because the sibling kernels use it.
/// </summary>
public sealed class SensorNoise
{
    public sealed record Channel(float[] Snr, float[] Sigma, float A, float B);
    public sealed record Model(int Iso, float Threshold, float Scale, Channel R, Channel G, Channel Bl, Channel? Pan);

    public SensorType Type { get; }
    public float Black { get; }
    public float White { get; }
    public float CliffSlope { get; }   // Sensor +0xc; entry +0xc — identified by elimination (the only other float in the proto) [?]
    public IReadOnlyList<Model> Models { get; }   // sorted by ISO (std::map)

    public const int TableStride = 0x38;
    private static readonly double[] Poly =   // DAT_18068b270 … 18068b2b8
    {
        1.430853e-06, 3.2172868e-07, -2.6295693e-05, -8.5123452e-05, -1.7851033e-05, 0.0020282884, 0.024377832, 0.037234715, 0.70309281, 0.16923658,
    };
    public const double VarianceFloor = 1e-10;   // DAT_18067eca8
    public const float MaxWhite = 65535f;        // DAT_1806823fc
    public const float IsoPerGain = 100f;        // DAT_18068b2d8

    public SensorNoise(SensorType type, SensorCharacterization sc)
    {
        if (type == SensorType.SensorUnknown) throw new InvalidOperationException("Unexpected sensor type!");
        if (sc.VstModel.Count == 0) throw new InvalidOperationException("Insufficient sensor data!");
        Type = type; Black = sc.BlackLevel; White = sc.WhiteLevel; CliffSlope = sc.HasCliffSlope ? sc.CliffSlope : 0f;
        if (White <= 0f || MaxWhite < White) throw new InvalidOperationException("invalid sensor white level value!");
        if (!(0f <= Black && Black < White)) throw new InvalidOperationException("invalid sensor black level value!");
        var models = new SortedDictionary<int, Model>();
        foreach (var m in sc.VstModel)
        {
            Channel? pan = m.Panchromatic is null ? null : BuildChannel(m.Panchromatic.A, m.Panchromatic.B);
            var model = new Model((int)m.Gain, m.Threshold, m.Scale, BuildChannel(m.Red.A, m.Red.B), BuildChannel(m.Green.A, m.Green.B), BuildChannel(m.Blue.A, m.Blue.B), pan);
            models[(int)m.Gain] = model;   // std::map: a repeated key replaces the tables of the existing node
        }
        Models = models.Values.ToList();
    }

    public bool IsMono => Type == SensorType.SensorAr1335Mono || Type == SensorType.SensorImx386Mono;

    /// <summary>`FUN_180120cb0`: ISO = (int)(gain · 100), first model with key ≥ ISO ("no noise model for ISO …").</summary>
    public Model ModelForGain(float analogGain)
    {
        int iso = (int)(analogGain * IsoPerGain);
        foreach (var m in Models) if (iso <= m.Iso) return m;
        throw new InvalidOperationException($"no noise model for ISO {iso}");
    }

    /// <summary>`FUN_180120f50`: σ tables per channel for the kernels — (R, G, B) for colour sensors, the panchromatic
    /// table for mono ("Mono sensor does not have panchromatic noise calibration!").</summary>
    public float[][] SigmaTables(float analogGain)
    {
        var m = ModelForGain(analogGain);
        if (IsMono)
        {
            if (m.Pan is null) throw new InvalidOperationException("Mono sensor does not have panchromatic noise calibration!");
            return new[] { m.Pan.Sigma };
        }
        return new[] { m.R.Sigma, m.G.Sigma, m.Bl.Sigma };
    }

    public Channel BuildChannel(float a, float b) => BuildChannel(a, b, Black, White, CliffSlope);

    /// <summary>`FUN_18011f5b0(sensor, out, a, b)` with `FUN_18011e550(a, b, N = (int)(white + 1), 0)`.</summary>
    public static Channel BuildChannel(float a, float b, float black, float white, float cliff)
    {
        int n = (int)(white + 1f);
        var t0 = new double[n];
        double da = a, db = b, invN = 1.0 / n;
        for (int i = 0; i < n; i++)
        {
            double x = (i + 0.5) * invN;
            double v = x * da + db;
            if (v <= VarianceFloor) v = VarianceFloor;
            double s = Math.Sqrt(v);
            double t = x * (1.0 / s);
            double p = ((((((((t * Poly[0] + Poly[1]) * t + Poly[2]) * t + Poly[3]) * t + Poly[4]) * t + Poly[5]) * t + Poly[6]) * t + Poly[7]) * t + Poly[8]) * t + Poly[9];
            t0[i] = s * 0.5 * (Math.Tanh(p) + 1.0);
        }
        var sig = new float[n];
        for (int i = 0; i < n; i++) sig[i] = (float)t0[i];
        // linear extrapolation below c = (int)(black · cliff)
        int c = (int)(black * cliff);
        if (c >= 0 && (float)c < white && c > 0)
        {
            float slope = (sig[c + 2] - sig[c - 2]) * 0.25f;   // DAT_180681ed0
            sig[0] = sig[c] - (float)c * slope;
            for (int j = 1; j < c; j++) sig[j] = sig[c] - (float)(c - j) * slope;
        }
        // SNR table from black upwards, filled below black with the value at black
        var snr = new float[n];
        int blk = (int)black;
        double invLen = 1.0 / n;
        for (int i = blk; i < n; i++) snr[i] = (float)(((double)i + 0.5) * invLen / (double)sig[i]);
        for (int i = 0; i < blk && i < n; i++) snr[i] = snr[blk];
        // σ to DN units
        for (int i = 0; i < n; i++) sig[i] *= white;
        return new Channel(snr, sig, a, b);
    }

    /// <summary>cp.dll's `lt::Sensor` models come ONLY from the built-in database (`CapturedImage` ctor `FUN_1801247b0` → `DAT_180831f40/f50`,
    /// ctors `FUN_18011eb90` colour / `FUN_18011fda0` mono); the LRI header's `sensor_data` is parsed but never used (verified live 2026-08-26:
    /// the ISO-375 AR1335 model is the built-in one). Black/white/cliff come from the first record (42 / 1023 / 2 for the AR1335).</summary>
    public static SensorNoise? FromHeader(LightHeader h, SensorType type) => FromDb(type);

    public static SensorNoise? FromDb(SensorType type) => type switch
    {
        SensorType.SensorAr1335 => new SensorNoise(type, SensorNoiseDb.Colour2, null),
        SensorType.SensorImx386 => new SensorNoise(type, SensorNoiseDb.Colour4, null),
        SensorType.SensorAr1335Mono => new SensorNoise(type, null, SensorNoiseDb.Mono3),
        SensorType.SensorImx386Mono => new SensorNoise(type, null, SensorNoiseDb.Mono5),
        _ => null,
    };

    static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private SensorNoise(SensorType type, SensorNoiseDb.ColourRec[]? colour, SensorNoiseDb.MonoRec[]? mono)
    {
        Type = type;
        var models = new SortedDictionary<int, Model>();
        if (colour is not null)
        {
            Black = colour[0].Black; White = colour[0].White; CliffSlope = F(colour[0].Cliff);
            if (colour.Length == 0) throw new InvalidOperationException("Insufficient sensor data!");
            foreach (var r in colour)
                models[r.Iso] = new Model(r.Iso, F(r.Threshold), F(r.Scale), BuildChannel(F(r.AR), F(r.BR)), BuildChannel(F(r.AG), F(r.BG)), BuildChannel(F(r.AB), F(r.BB)), null);
        }
        else
        {
            var m0 = mono![0]; Black = F(m0.Black); White = F(m0.White); CliffSlope = F(m0.Cliff);
            foreach (var r in mono)
            {
                var pan = BuildChannel(F(r.A), F(r.B));
                models[r.Iso] = new Model(r.Iso, F(r.Threshold), F(r.Scale), pan, pan, pan, pan);
            }
        }
        if (White <= 0f || MaxWhite < White) throw new InvalidOperationException("invalid sensor white level value!");
        if (!(0f <= Black && Black < White)) throw new InvalidOperationException("invalid sensor black level value!");
        Models = models.Values.ToList();
    }
}
