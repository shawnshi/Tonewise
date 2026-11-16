using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// 64-bit floating-point DSP stage supporting ReplayGain, anti-clipping preamp,
/// ISO 226 dynamic equal-loudness, dual-channel L/R 64-band Biquad PEQ,
/// partitioned FIR convolution, ±1 LSB TPDF dithering, and bit-perfect bypass.
/// </summary>
public sealed class DspSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Lock _dspLock = new();
    private readonly DualChannelBiquadNode _biquadNode = new();
    private readonly Iso226LoudnessNode _iso226;
    private readonly PartitionedFirConvolver _firConvolver;
    private readonly SandboxedVst3HostBridge _vst3Bridge = new();
    private readonly TpdfDitherProcessor _dither = new();

    private double _preampLinear = 1.0;
    private volatile bool _bypass;

    public DspSampleProvider(ISampleProvider source)
    {
        _source = source;
        _iso226 = new Iso226LoudnessNode(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
        _firConvolver = new PartitionedFirConvolver(source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool IsBypassed
    {
        get => _bypass;
        set => _bypass = value;
    }

    public double EffectivePreampDb { get; private set; }

    public int ActiveFilterCount
    {
        get
        {
            lock (_dspLock) return _biquadNode.TotalSpecCount;
        }
    }

    public bool IsIso226Active
    {
        get
        {
            lock (_dspLock) return _iso226.IsEnabled && _iso226.VolumeAttenuationDb < -0.5;
        }
    }

    public int FirTapCount
    {
        get
        {
            lock (_dspLock) return _firConvolver.IsEnabled ? _firConvolver.LoadedTapCount : 0;
        }
    }

    public bool IsTpdfDitherEnabled
    {
        get => _dither.IsEnabled;
        set => _dither.IsEnabled = value;
    }

    public int ActiveVst3Count
    {
        get
        {
            lock (_dspLock) return _vst3Bridge.ActiveSlotCount;
        }
    }

    public bool IsVst3FaultedBypass => _vst3Bridge.IsFaultedBypass;

    public void ConfigureVst3Slots(
        IEnumerable<Vst3SlotConfig> slots,
        Func<double, int, int, double>? externalWorkerCallback = null)
    {
        lock (_dspLock)
        {
            _vst3Bridge.ConfigureSlots(slots, externalWorkerCallback);
        }
    }

    public void Configure(EqualizerApoProfile? profile, double replayGainDb)
    {
        double effectiveDb = profile is not null
            ? profile.ComputeEffectivePreampDb(replayGainDb)
            : replayGainDb;
        double linear = Math.Pow(10.0, effectiveDb / 20.0);

        lock (_dspLock)
        {
            EffectivePreampDb = effectiveDb;
            _preampLinear = linear;
            _biquadNode.Configure(profile, WaveFormat.SampleRate, WaveFormat.Channels);
        }
    }

    public void ConfigureIso226(bool isEnabled, double referenceSplDb, double volumeAttenuationDb)
    {
        lock (_dspLock)
        {
            _iso226.Configure(isEnabled, referenceSplDb, volumeAttenuationDb);
        }
    }

    public void ConfigureFirImpulse(double[]? leftIr, double[]? rightIr, double dryWetMix = 1.0, string? sourceLabel = null)
    {
        lock (_dspLock)
        {
            if (leftIr is null || rightIr is null || leftIr.Length == 0 || rightIr.Length == 0)
            {
                _firConvolver.Clear();
            }
            else
            {
                _firConvolver.DryWetMix = dryWetMix;
                _firConvolver.LoadImpulse(leftIr, rightIr, normalizeEnergy: true, sourceLabel: sourceLabel);
            }
        }
    }

    public void ConfigureFirFromWav(string? wavPath, double dryWetMix = 1.0)
    {
        lock (_dspLock)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            {
                _firConvolver.Clear();
            }
            else
            {
                _firConvolver.DryWetMix = dryWetMix;
                _firConvolver.LoadImpulseFromSofaOrWav(wavPath, normalizeEnergy: true);
            }
        }
    }

    public void ConfigureDither(bool isEnabled, int targetBitDepth = 24)
    {
        lock (_dspLock)
        {
            _dither.IsEnabled = isEnabled;
            _dither.TargetBitDepth = targetBitDepth;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0 || _bypass)
        {
            return read;
        }

        int channels = Math.Max(WaveFormat.Channels, 1);
        lock (_dspLock)
        {
            bool anyEq = _biquadNode.TotalSpecCount > 0;
            bool anyGain = Math.Abs(_preampLinear - 1.0) >= 1e-9;
            bool anyIso = _iso226.IsEnabled && _iso226.VolumeAttenuationDb < -0.5;
            bool anyFir = _firConvolver.IsEnabled && _firConvolver.LoadedTapCount > 0;
            bool anyVst3 = _vst3Bridge.ActiveSlotCount > 0 && _vst3Bridge.IsWorkerConnected;

            if (!anyEq && !anyGain && !anyIso && !anyFir && !anyVst3)
            {
                return read;
            }

            double gain = _preampLinear;
            for (int i = 0; i < read; i++)
            {
                int ch = i % channels;
                double sample = buffer[offset + i] * gain;
                sample = _iso226.ProcessSample(sample, ch);
                sample = _biquadNode.ProcessSample(sample, ch);
                sample = _firConvolver.ProcessSample(sample, ch);
                sample = _vst3Bridge.ProcessSample(sample, ch);
                sample = _dither.ProcessSample(sample);
                buffer[offset + i] = (float)sample;
            }
        }

        return read;
    }
}
