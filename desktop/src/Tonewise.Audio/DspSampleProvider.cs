using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// 64-bit floating-point DSP stage supporting ReplayGain, anti-clipping preamp,
/// cascaded Biquad PEQ filters, and zero-modification bit-perfect bypass.
/// </summary>
public sealed class DspSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Lock _dspLock = new();
    private List<BiquadFilter64> _filters = [];
    private double _preampLinear = 1.0;
    private volatile bool _bypass;

    public DspSampleProvider(ISampleProvider source)
    {
        _source = source;
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
            lock (_dspLock) return _filters.Count;
        }
    }

    public void Configure(EqualizerApoProfile? profile, double replayGainDb)
    {
        double effectiveDb = profile is not null
            ? profile.ComputeEffectivePreampDb(replayGainDb)
            : replayGainDb;
        double linear = Math.Pow(10.0, effectiveDb / 20.0);

        var built = new List<BiquadFilter64>();
        if (profile is not null)
        {
            foreach (var spec in profile.Filters)
            {
                built.Add(new BiquadFilter64(spec, WaveFormat.SampleRate, WaveFormat.Channels));
            }
        }

        lock (_dspLock)
        {
            EffectivePreampDb = effectiveDb;
            _preampLinear = linear;
            _filters = built;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0 || _bypass)
        {
            return read;
        }

        List<BiquadFilter64> filters;
        double gain;
        lock (_dspLock)
        {
            filters = _filters;
            gain = _preampLinear;
        }

        if (filters.Count == 0 && Math.Abs(gain - 1.0) < 1e-9)
        {
            return read;
        }

        int channels = Math.Max(WaveFormat.Channels, 1);
        for (int i = 0; i < read; i++)
        {
            int ch = i % channels;
            double sample = buffer[offset + i] * gain;
            for (int f = 0; f < filters.Count; f++)
            {
                sample = filters[f].ProcessSample(sample, ch);
            }
            buffer[offset + i] = (float)sample;
        }

        return read;
    }
}
