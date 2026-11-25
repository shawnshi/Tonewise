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

public enum BiquadChannelScope
{
    All = 0,
    Left = 1,
    Right = 2,
}

public sealed record BiquadSpec(
    BiquadKind Kind,
    double FrequencyHz,
    double GainDb,
    double Q,
    BiquadChannelScope Channel = BiquadChannelScope.All
);

public sealed record EqualizerApoProfile(
    double PreampDb,
    IReadOnlyList<BiquadSpec> Filters,
    string? BoundDeviceName = null,
    string? FirImpulsePath = null,
    bool EnableIso226 = false,
    double ReferenceSplDb = 80.0
)
{
    public const int MaxBandsPerChannel = 64;

    public double ComputeEffectivePreampDb(double replayGainDb)
    {
        double maxBoostL = 0.0;
        double maxBoostR = 0.0;
        foreach (var f in Filters)
        {
            if (f.GainDb <= 0.0) continue;
            if (f.Channel is BiquadChannelScope.All or BiquadChannelScope.Left && f.GainDb > maxBoostL)
            {
                maxBoostL = f.GainDb;
            }
            if (f.Channel is BiquadChannelScope.All or BiquadChannelScope.Right && f.GainDb > maxBoostR)
            {
                maxBoostR = f.GainDb;
            }
        }
        double maxBoost = Math.Max(maxBoostL, maxBoostR);
        double rgEnvelope = replayGainDb - maxBoost;
        return Math.Min(rgEnvelope, PreampDb + replayGainDb);
    }

    public static EqualizerApoProfile Parse(string text)
    {
        double preampDb = 0.0;
        var filters = new List<BiquadSpec>();
        var currentScope = BiquadChannelScope.All;
        int leftCount = 0;
        int rightCount = 0;
        string? boundDevice = null;
        string? firPath = null;
        bool enableIso226 = false;
        double refSpl = 80.0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("# Tonewise-Meta:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseMetaHeader(
                        line["# Tonewise-Meta:".Length..],
                        ref boundDevice,
                        ref firPath,
                        ref enableIso226,
                        ref refSpl
                    );
                }
                continue;
            }

            if (line.StartsWith("Channel:", StringComparison.OrdinalIgnoreCase))
            {
                string chan = line["Channel:".Length..].Trim().ToUpperInvariant();
                currentScope = chan switch
                {
                    "L" or "1" or "LEFT" => BiquadChannelScope.Left,
                    "R" or "2" or "RIGHT" => BiquadChannelScope.Right,
                    _ => BiquadChannelScope.All,
                };
            }
            else if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
            {
                string num = line["Preamp:".Length..].Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                {
                    preampDb = db;
                }
            }
            else if (TryExtractFilterPayload(line, out string payload))
            {
                string[] tokens = payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
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
                    bool allow = currentScope switch
                    {
                        BiquadChannelScope.Left => leftCount < MaxBandsPerChannel,
                        BiquadChannelScope.Right => rightCount < MaxBandsPerChannel,
                        _ => leftCount < MaxBandsPerChannel && rightCount < MaxBandsPerChannel,
                    };
                    if (!allow) continue;

                    if (currentScope is BiquadChannelScope.All or BiquadChannelScope.Left) leftCount++;
                    if (currentScope is BiquadChannelScope.All or BiquadChannelScope.Right) rightCount++;

                    filters.Add(new BiquadSpec(kind.Value, fc.Value, gain, Math.Max(q, 0.05), currentScope));
                }
            }
        }

        return new EqualizerApoProfile(preampDb, filters, boundDevice, firPath, enableIso226, refSpl);
    }

    private static void ParseMetaHeader(
        string metaBody,
        ref string? boundDevice,
        ref string? firPath,
        ref bool enableIso226,
        ref double refSpl)
    {
        foreach (string part in metaBody.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eqIdx = part.IndexOf('=');
            if (eqIdx <= 0) continue;
            string key = part[..eqIdx].Trim();
            string val = part[(eqIdx + 1)..].Trim();
            if (key.Equals("Device", StringComparison.OrdinalIgnoreCase))
            {
                boundDevice = string.IsNullOrWhiteSpace(val) ? null : val;
            }
            else if (key.Equals("FirPath", StringComparison.OrdinalIgnoreCase))
            {
                firPath = string.IsNullOrWhiteSpace(val) ? null : val;
            }
            else if (key.Equals("Iso226", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool iso))
            {
                enableIso226 = iso;
            }
            else if (key.Equals("RefSpl", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double spl))
            {
                refSpl = Math.Clamp(spl, 70.0, 90.0);
            }
        }
    }

    private static bool TryExtractFilterPayload(string line, out string payload)
    {
        payload = "";
        if (!line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase)) return false;
        int colonIdx = line.IndexOf(':');
        if (colonIdx < 6) return false;
        ReadOnlySpan<char> mid = line.AsSpan(6, colonIdx - 6).Trim();
        if (!mid.IsEmpty)
        {
            foreach (char c in mid)
            {
                if (!char.IsAsciiDigit(c)) return false;
            }
        }
        payload = line[(colonIdx + 1)..];
        return true;
    }
}

/// <summary>
/// 64-bit IEEE-754 double-precision Direct Form II Transposed Biquad IIR filter.
/// </summary>
public sealed class BiquadFilter64
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _tb0, _tb1, _tb2, _ta1, _ta2;
    private double _db0, _db1, _db2, _da1, _da2;
    private int _rampRemaining;
    private bool _initialized;
    private readonly int _channels;
    private readonly double[] _z1;
    private readonly double[] _z2;

    public BiquadChannelScope ChannelScope { get; private set; }

    public BiquadFilter64(BiquadSpec spec, int sampleRateHz, int channels)
    {
        _channels = Math.Max(channels, 1);
        _z1 = new double[_channels];
        _z2 = new double[_channels];
        UpdateCoefficients(spec, sampleRateHz);
    }

    public void UpdateCoefficients(BiquadSpec spec, int sampleRateHz)
    {
        ChannelScope = spec.Channel;
        double fs = Math.Max(sampleRateHz, 8000);
        double fc = Math.Clamp(spec.FrequencyHz, 10.0, fs * 0.49);
        double w0 = 2.0 * Math.PI * fc / fs;
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

        _tb0 = b0 / a0;
        _tb1 = b1 / a0;
        _tb2 = b2 / a0;
        _ta1 = a1 / a0;
        _ta2 = a2 / a0;

        if (!_initialized)
        {
            _b0 = _tb0;
            _b1 = _tb1;
            _b2 = _tb2;
            _a1 = _ta1;
            _a2 = _ta2;
            _rampRemaining = 0;
            _initialized = true;
            return;
        }

        double maxDiff = Math.Max(
            Math.Abs(_tb0 - _b0),
            Math.Max(Math.Abs(_tb1 - _b1),
                Math.Max(Math.Abs(_tb2 - _b2), Math.Max(Math.Abs(_ta1 - _a1), Math.Abs(_ta2 - _a2)))));
        if (maxDiff < 1e-10)
        {
            _b0 = _tb0;
            _b1 = _tb1;
            _b2 = _tb2;
            _a1 = _ta1;
            _a2 = _ta2;
            _rampRemaining = 0;
            return;
        }

        int rampSamples = Math.Max(1, (int)Math.Round(0.015 * Math.Max(sampleRateHz, 8000)));
        _rampRemaining = rampSamples;
        _db0 = (_tb0 - _b0) / rampSamples;
        _db1 = (_tb1 - _b1) / rampSamples;
        _db2 = (_tb2 - _b2) / rampSamples;
        _da1 = (_ta1 - _a1) / rampSamples;
        _da2 = (_ta2 - _a2) / rampSamples;
    }

    public double ProcessSample(double x, int channel)
    {
        if (_rampRemaining > 0)
        {
            int stepChannel = (_channels > 1 && ChannelScope == BiquadChannelScope.Right) ? 1 : 0;
            if (channel == stepChannel || _channels == 1)
            {
                if (--_rampRemaining == 0)
                {
                    _b0 = _tb0;
                    _b1 = _tb1;
                    _b2 = _tb2;
                    _a1 = _ta1;
                    _a2 = _ta2;
                }
                else
                {
                    _b0 += _db0;
                    _b1 += _db1;
                    _b2 += _db2;
                    _a1 += _da1;
                    _a2 += _da2;
                }
            }
        }

        double y = _b0 * x + _z1[channel];
        _z1[channel] = _b1 * x - _a1 * y + _z2[channel];
        _z2[channel] = _b2 * x - _a2 * y;
        return y;
    }

    public double MagnitudeAtHz(double freqHz, int sampleRateHz)
    {
        double w = 2.0 * Math.PI * freqHz / Math.Max(sampleRateHz, 8000);
        double cw = Math.Cos(w);
        double sw = Math.Sin(w);
        double c2w = Math.Cos(2.0 * w);
        double s2w = Math.Sin(2.0 * w);
        double numRe = _tb0 + _tb1 * cw + _tb2 * c2w;
        double numIm = -(_tb1 * sw + _tb2 * s2w);
        double denRe = 1.0 + _ta1 * cw + _ta2 * c2w;
        double denIm = -(_ta1 * sw + _ta2 * s2w);
        double numMag = Math.Sqrt(numRe * numRe + numIm * numIm);
        double denMag = Math.Sqrt(denRe * denRe + denIm * denIm);
        return denMag > 1e-12 ? numMag / denMag : 1.0;
    }

    public void Reset()
    {
        _b0 = _tb0;
        _b1 = _tb1;
        _b2 = _tb2;
        _a1 = _ta1;
        _a2 = _ta2;
        _rampRemaining = 0;
        Array.Clear(_z1);
        Array.Clear(_z2);
    }
}

/// <summary>
/// Dual-channel 64-bit PEQ bank supporting stereo-linked (`All`) and independent `L`/`R` bands.
/// </summary>
public sealed class DualChannelBiquadNode
{
    private readonly List<BiquadFilter64> _leftChain = [];
    private readonly List<BiquadFilter64> _rightChain = [];
    private readonly List<BiquadFilter64> _extraChain = [];
    private int _configuredSampleRateHz;
    private int _configuredChannels;

    public int TotalSpecCount { get; private set; }
    public bool HasActiveNonZeroFilter { get; private set; }
    public int LeftBandCount => _leftChain.Count;
    public int RightBandCount => _rightChain.Count;

    public void Configure(EqualizerApoProfile? profile, int sampleRateHz, int channels)
    {
        if (profile is null)
        {
            _leftChain.Clear();
            _rightChain.Clear();
            _extraChain.Clear();
            TotalSpecCount = 0;
            HasActiveNonZeroFilter = false;
            _configuredSampleRateHz = sampleRateHz;
            _configuredChannels = channels;
            return;
        }

        var leftSpecs = new List<BiquadSpec>(profile.Filters.Count);
        var rightSpecs = new List<BiquadSpec>(profile.Filters.Count);
        var extraSpecs = new List<BiquadSpec>();
        bool anyActive = false;

        foreach (var spec in profile.Filters)
        {
            if (Math.Abs(spec.GainDb) > 1e-4 || spec.Kind is BiquadKind.LowPass or BiquadKind.HighPass)
            {
                anyActive = true;
            }
            if (spec.Channel is BiquadChannelScope.All or BiquadChannelScope.Left) leftSpecs.Add(spec);
            if (spec.Channel is BiquadChannelScope.All or BiquadChannelScope.Right) rightSpecs.Add(spec);
            if (channels > 2 && spec.Channel == BiquadChannelScope.All) extraSpecs.Add(spec);
        }

        TotalSpecCount = profile.Filters.Count;
        HasActiveNonZeroFilter = anyActive;
        bool canHotUpdate = _configuredSampleRateHz == sampleRateHz
            && _configuredChannels == channels
            && _leftChain.Count == leftSpecs.Count
            && _rightChain.Count == rightSpecs.Count
            && _extraChain.Count == extraSpecs.Count;

        if (canHotUpdate)
        {
            for (int i = 0; i < leftSpecs.Count; i++) _leftChain[i].UpdateCoefficients(leftSpecs[i], sampleRateHz);
            for (int i = 0; i < rightSpecs.Count; i++) _rightChain[i].UpdateCoefficients(rightSpecs[i], sampleRateHz);
            for (int i = 0; i < extraSpecs.Count; i++) _extraChain[i].UpdateCoefficients(extraSpecs[i], sampleRateHz);
            return;
        }

        _configuredSampleRateHz = sampleRateHz;
        _configuredChannels = channels;
        _leftChain.Clear();
        _rightChain.Clear();
        _extraChain.Clear();

        foreach (var s in leftSpecs) _leftChain.Add(new BiquadFilter64(s, sampleRateHz, channels));
        foreach (var s in rightSpecs) _rightChain.Add(new BiquadFilter64(s, sampleRateHz, channels));
        foreach (var s in extraSpecs) _extraChain.Add(new BiquadFilter64(s, sampleRateHz, channels));
    }

    public double ProcessSample(double x, int channel)
    {
        List<BiquadFilter64> chain = channel switch
        {
            0 => _leftChain,
            1 => _rightChain,
            _ => _extraChain,
        };

        double sample = x;
        for (int i = 0; i < chain.Count; i++)
        {
            sample = chain[i].ProcessSample(sample, channel);
        }
        return sample;
    }

    public void Reset()
    {
        foreach (var f in _leftChain) f.Reset();
        foreach (var f in _rightChain) f.Reset();
        foreach (var f in _extraChain) f.Reset();
    }
}
