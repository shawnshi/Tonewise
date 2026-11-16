using System.Globalization;

namespace Tonewise.Audio;

public enum BiquadKind
{
    Peaking,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
}

public sealed record BiquadSpec(BiquadKind Kind, double FrequencyHz, double GainDb, double Q);

public sealed record EqualizerApoProfile(double PreampDb, IReadOnlyList<BiquadSpec> Filters)
{
    public double ComputeEffectivePreampDb(double replayGainDb)
    {
        double maxBoost = 0.0;
        foreach (var f in Filters)
        {
            if (f.GainDb > maxBoost) maxBoost = f.GainDb;
        }
        double rgEnvelope = replayGainDb - maxBoost;
        return Math.Min(rgEnvelope, PreampDb + replayGainDb);
    }

    public static EqualizerApoProfile Parse(string text)
    {
        double preampDb = 0.0;
        var filters = new List<BiquadSpec>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
            {
                string num = line["Preamp:".Length..].Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                {
                    preampDb = db;
                }
            }
            else if (line.StartsWith("Filter:", StringComparison.OrdinalIgnoreCase))
            {
                string[] tokens = line["Filter:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 4 || !tokens[0].Equals("ON", StringComparison.OrdinalIgnoreCase))
                    continue;

                BiquadKind? kind = tokens[1].ToUpperInvariant() switch
                {
                    "PK" => BiquadKind.Peaking,
                    "LSC" or "LS" => BiquadKind.LowShelf,
                    "HSC" or "HS" => BiquadKind.HighShelf,
                    "LP" or "LPQ" => BiquadKind.LowPass,
                    "HP" or "HPQ" => BiquadKind.HighPass,
                    _ => null,
                };
                if (kind is null) continue;

                double? fc = null;
                double gain = 0.0;
                double q = 0.707;
                for (int i = 2; i + 1 < tokens.Length;)
                {
                    switch (tokens[i])
                    {
                        case "Fc" when double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double f):
                            fc = f; i += 2; break;
                        case "Gain" when double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double g):
                            gain = g; i += 2; break;
                        case "Q" when double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double qv):
                            q = qv; i += 2; break;
                        default:
                            i++; break;
                    }
                }
                if (fc is > 0)
                {
                    filters.Add(new BiquadSpec(kind.Value, fc.Value, gain, Math.Max(q, 0.05)));
                }
            }
        }

        return new EqualizerApoProfile(preampDb, filters);
    }
}

/// <summary>
/// 64-bit IEEE-754 double-precision Direct Form II Transposed Biquad IIR filter.
/// </summary>
public sealed class BiquadFilter64
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private readonly double[] _z1;
    private readonly double[] _z2;

    public BiquadFilter64(BiquadSpec spec, int sampleRateHz, int channels)
    {
        _z1 = new double[Math.Max(channels, 1)];
        _z2 = new double[Math.Max(channels, 1)];

        double fc = Math.Clamp(spec.FrequencyHz, 10.0, sampleRateHz * 0.49);
        double w0 = 2.0 * Math.PI * fc / sampleRateHz;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double q = Math.Max(spec.Q, 0.05);
        double alpha = sinW0 / (2.0 * q);
        double a = Math.Pow(10.0, spec.GainDb / 40.0);

        double b0, b1, b2, a0, a1, a2;
        switch (spec.Kind)
        {
            case BiquadKind.Peaking:
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cosW0;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha / a;
                break;
            case BiquadKind.LowShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0);
                a2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }
            case BiquadKind.HighShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
                a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }
            case BiquadKind.LowPass:
                b0 = (1.0 - cosW0) / 2.0;
                b1 = 1.0 - cosW0;
                b2 = (1.0 - cosW0) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;
            case BiquadKind.HighPass:
                b0 = (1.0 + cosW0) / 2.0;
                b1 = -(1.0 + cosW0);
                b2 = (1.0 + cosW0) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;
            default:
                b0 = 1.0; b1 = 0.0; b2 = 0.0; a0 = 1.0; a1 = 0.0; a2 = 0.0;
                break;
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public double ProcessSample(double x, int channel)
    {
        double y = _b0 * x + _z1[channel];
        _z1[channel] = _b1 * x - _a1 * y + _z2[channel];
        _z2[channel] = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }
}
