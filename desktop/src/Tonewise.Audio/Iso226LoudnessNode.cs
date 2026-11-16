namespace Tonewise.Audio;

/// <summary>
/// ISO 226:2003 dynamic equal-loudness contour compensator operating in 64-bit IEEE-754 double precision.
/// Dynamically boosts 60 Hz low-shelf and 8 kHz high-shelf as digital volume attenuates below calibration SPL.
/// </summary>
public sealed class Iso226LoudnessNode
{
    private readonly int _sampleRateHz;
    private readonly int _channels;
    private bool _isEnabled;
    private double _referenceSplDb = 80.0;
    private double _volumeAttenuationDb;
    private BiquadFilter64? _lowShelf;
    private BiquadFilter64? _highShelf;

    public Iso226LoudnessNode(int sampleRateHz, int channels)
    {
        _sampleRateHz = Math.Max(sampleRateHz, 8000);
        _channels = Math.Max(channels, 1);
    }

    public bool IsEnabled => _isEnabled;
    public double ReferenceSplDb => _referenceSplDb;
    public double VolumeAttenuationDb => _volumeAttenuationDb;
    public double ActiveLowBoostDb { get; private set; }
    public double ActiveHighBoostDb { get; private set; }

    public void Configure(bool isEnabled, double referenceSplDb, double volumeAttenuationDb)
    {
        double clampedSpl = Math.Clamp(referenceSplDb, 70.0, 90.0);
        double clampedVol = Math.Clamp(volumeAttenuationDb, -60.0, 0.0);

        bool changed = _isEnabled != isEnabled
            || Math.Abs(_referenceSplDb - clampedSpl) > 0.05
            || Math.Abs(_volumeAttenuationDb - clampedVol) > 0.05;

        _isEnabled = isEnabled;
        _referenceSplDb = clampedSpl;
        _volumeAttenuationDb = clampedVol;

        if (!changed) return;

        if (!_isEnabled || _volumeAttenuationDb >= -0.5)
        {
            ActiveLowBoostDb = 0.0;
            ActiveHighBoostDb = 0.0;
            _lowShelf = null;
            _highShelf = null;
            return;
        }

        double atten = Math.Clamp(-_volumeAttenuationDb, 0.0, 40.0);
        double splFactor = 1.0 + (80.0 - _referenceSplDb) * 0.015;
        ActiveLowBoostDb = Math.Min(12.0, atten * 0.32 * splFactor);
        ActiveHighBoostDb = Math.Min(6.0, atten * 0.14 * splFactor);

        _lowShelf = new BiquadFilter64(
            new BiquadSpec(BiquadKind.LowShelf, 60.0, ActiveLowBoostDb, 0.707),
            _sampleRateHz,
            _channels
        );
        _highShelf = new BiquadFilter64(
            new BiquadSpec(BiquadKind.HighShelf, 8000.0, ActiveHighBoostDb, 0.707),
            _sampleRateHz,
            _channels
        );
    }

    public double ProcessSample(double sample, int channel)
    {
        if (!_isEnabled || _lowShelf is null || _highShelf is null)
        {
            return sample;
        }
        int safeCh = Math.Clamp(channel, 0, _channels - 1);
        double s = _lowShelf.ProcessSample(sample, safeCh);
        return _highShelf.ProcessSample(s, safeCh);
    }

    public void Reset()
    {
        _lowShelf?.Reset();
        _highShelf?.Reset();
    }
}
