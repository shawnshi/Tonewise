using System.Text.Json;

namespace Tonewise.Audio;

public enum DesktopDspPresetId
{
    Flat,
    OpenEarBluetooth,
    FocusNight,
    ClassicalCrossfeed,
    Custom,
}

public sealed record DesktopPeqBand(
    BiquadKind Kind,
    double FrequencyHz,
    double GainDb,
    double Q = 1.41,
    bool Enabled = true
);

public sealed record DesktopDspChainConfig(
    DesktopDspPresetId PresetId = DesktopDspPresetId.Flat,
    bool Enabled = false,
    bool GainMatchedBypass = false,
    double PreampDb = 0.0,
    IReadOnlyList<DesktopPeqBand>? Bands = null,
    bool CrossfeedEnabled = false,
    double CrossfeedCutoffHz = 700.0,
    double CrossfeedFeedDb = 4.5,
    double CrossfeedDelayMs = 0.28,
    double BalanceDb = 0.0,
    bool MonoFold = false,
    bool LimiterEnabled = true,
    double LimiterCeilingDb = -0.2
)
{
    public static readonly double[] StandardTenBandFrequencies =
        [31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0];

    public IReadOnlyList<DesktopPeqBand> EffectiveBands => Bands is { Count: > 0 } ? Bands : DefaultTenBands();

    public static IReadOnlyList<DesktopPeqBand> DefaultTenBands() =>
        StandardTenBandFrequencies
            .Select(f => new DesktopPeqBand(BiquadKind.Peaking, f, 0.0, 1.41, true))
            .ToList();

    public bool HasNonIdentityProcessing()
    {
        if (!Enabled) return false;
        if (GainMatchedBypass) return true;
        if (Math.Abs(PreampDb) > 1e-4) return true;
        if (EffectiveBands.Any(b => b.Enabled && (Math.Abs(b.GainDb) > 1e-4 || b.Kind is BiquadKind.LowPass or BiquadKind.HighPass)))
            return true;
        if (CrossfeedEnabled) return true;
        if (Math.Abs(BalanceDb) > 1e-4 || MonoFold) return true;
        return false;
    }

    public EqualizerApoProfile ToEqualizerApoProfile()
    {
        var specs = EffectiveBands
            .Where(b => b.Enabled && (Math.Abs(b.GainDb) > 1e-4 || b.Kind is BiquadKind.LowPass or BiquadKind.HighPass))
            .Select(b => new BiquadSpec(b.Kind, b.FrequencyHz, b.GainDb, b.Q, BiquadChannelScope.All))
            .ToList();
        return new EqualizerApoProfile(PreampDb, specs);
    }

    public static DesktopDspChainConfig CreatePreset(DesktopDspPresetId id) => id switch
    {
        DesktopDspPresetId.OpenEarBluetooth => new DesktopDspChainConfig(
            PresetId: DesktopDspPresetId.OpenEarBluetooth,
            Enabled: true,
            PreampDb: -4.5,
            Bands:
            [
                new(BiquadKind.Peaking, 31.0, 2.5, 1.0, true),
                new(BiquadKind.LowShelf, 62.0, 4.0, 0.71, true),
                new(BiquadKind.Peaking, 125.0, 1.0, 1.2, true),
                new(BiquadKind.Peaking, 250.0, -1.0, 1.2, true),
                new(BiquadKind.Peaking, 500.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 1000.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 2000.0, 1.0, 1.4, true),
                new(BiquadKind.Peaking, 4000.0, 0.5, 1.41, true),
                new(BiquadKind.HighShelf, 8000.0, 1.5, 0.71, true),
                new(BiquadKind.Peaking, 16000.0, 0.5, 1.41, true),
            ],
            LimiterEnabled: true
        ),
        DesktopDspPresetId.FocusNight => new DesktopDspChainConfig(
            PresetId: DesktopDspPresetId.FocusNight,
            Enabled: true,
            PreampDb: -1.5,
            Bands:
            [
                new(BiquadKind.Peaking, 31.0, 0.0, 1.41, true),
                new(BiquadKind.LowShelf, 62.0, 1.5, 0.71, true),
                new(BiquadKind.Peaking, 125.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 250.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 500.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 1000.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 2000.0, 0.0, 1.41, true),
                new(BiquadKind.Peaking, 4000.0, -1.0, 1.41, true),
                new(BiquadKind.HighShelf, 8000.0, -2.5, 0.71, true),
                new(BiquadKind.Peaking, 16000.0, -3.0, 1.41, true),
            ],
            CrossfeedEnabled: true,
            CrossfeedCutoffHz: 700.0,
            CrossfeedFeedDb: 4.5,
            LimiterEnabled: true
        ),
        DesktopDspPresetId.ClassicalCrossfeed => new DesktopDspChainConfig(
            PresetId: DesktopDspPresetId.ClassicalCrossfeed,
            Enabled: true,
            PreampDb: 0.0,
            Bands: DefaultTenBands(),
            CrossfeedEnabled: true,
            CrossfeedCutoffHz: 700.0,
            CrossfeedFeedDb: 4.5,
            CrossfeedDelayMs: 0.28,
            LimiterEnabled: true
        ),
        DesktopDspPresetId.Custom => new DesktopDspChainConfig(
            PresetId: DesktopDspPresetId.Custom,
            Enabled: true,
            Bands: DefaultTenBands(),
            LimiterEnabled: true
        ),
        _ => new DesktopDspChainConfig(
            PresetId: DesktopDspPresetId.Flat,
            Enabled: false,
            Bands: DefaultTenBands(),
            LimiterEnabled: true
        ),
    };

    /// <summary>
    /// 24-point log-spaced perceptual loudness delta estimate (40 Hz .. 14 kHz) matching Android `TonewiseDspEngine`.
    /// </summary>
    public static double EstimateRmsLoudnessShiftDb(EqualizerApoProfile? profile, int sampleRateHz = 48000)
    {
        if (profile is null) return 0.0;
        var active = profile.Filters
            .Where(f => Math.Abs(f.GainDb) > 1e-4)
            .Select(f => new BiquadFilter64(f, sampleRateHz, 1))
            .ToList();
        double preampShift = profile.ComputeEffectivePreampDb(0.0);
        if (active.Count == 0) return Math.Clamp(preampShift, -15.0, 6.0);

        const int steps = 24;
        double fMin = Math.Log(40.0);
        double fMax = Math.Log(Math.Min(14000.0, Math.Max(sampleRateHz, 8000) * 0.45));
        double powerSum = 0.0;
        for (int i = 0; i < steps; i++)
        {
            double freq = Math.Exp(fMin + (fMax - fMin) * i / (steps - 1));
            double mag = 1.0;
            foreach (var f in active)
            {
                mag *= f.MagnitudeAtHz(freq, sampleRateHz);
            }
            powerSum += mag * mag;
        }
        double rmsDb = 10.0 * Math.Log10(Math.Max(powerSum / steps, 1e-6));
        return Math.Clamp(preampShift + rmsDb, -15.0, 6.0);
    }

    public static string SerializeDeviceProfileMap(IReadOnlyDictionary<string, DesktopDspChainConfig> map) =>
        JsonSerializer.Serialize(map);

    public static Dictionary<string, DesktopDspChainConfig> DeserializeDeviceProfileMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, DesktopDspChainConfig>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, DesktopDspChainConfig>>(json);
            return parsed is null
                ? new Dictionary<string, DesktopDspChainConfig>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DesktopDspChainConfig>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, DesktopDspChainConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
