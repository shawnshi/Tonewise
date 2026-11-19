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
    private double _masterLinear = 1.0;
    private EqualizerApoProfile? _lastProfile;
    private double _lastReplayGainDb;
    private volatile bool _bypass;
    private volatile bool _reachedEndOfStream;

    // Extended stereo acoustic stages (Gain-Matched A/B, Bauer Crossfeed, Balance/Mono, 3ms Lookahead Limiter)
    private bool _gainMatchedBypass;
    private double _rmsShiftDb;
    private bool _crossfeedEnabled;
    private double _crossLpL;
    private double _crossLpR;
    private double _crossAlphaLp;
    private double _crossDirectGain = 1.0;
    private double _crossFeedGain;
    private int _crossDelaySamples = 12;
    private double[] _crossDelayBufL = new double[64];
    private double[] _crossDelayBufR = new double[64];
    private int _crossDelayPos;
    private double _balanceDb;
    private double _leftBalanceGain = 1.0;
    private double _rightBalanceGain = 1.0;
    private bool _monoFold;
    private bool _limiterEnabled;
    private double _limiterCeilingDb = -0.2;
    private double _limiterCeilingLinear = Math.Pow(10.0, -0.2 / 20.0);
    private int _limiterDelaySamples = 144;
    private double[][] _limiterDelayBuf = [];
    private int _limiterDelayPos;
    private double _limiterEnvelope = 1.0;
    private double _limiterReleaseCoeff = 0.999;
    private double[] _frameWork = new double[8];

    public DspSampleProvider(ISampleProvider source)
    {
        _source = source;
        int sr = Math.Max(source.WaveFormat.SampleRate, 8000);
        int ch = Math.Max(source.WaveFormat.Channels, 1);
        _iso226 = new Iso226LoudnessNode(sr, ch);
        _firConvolver = new PartitionedFirConvolver(ch);
        InitExtendedBuffersUnlocked(sr, ch, 700.0, 4.5, 0.28, -0.2);
    }

    private void InitExtendedBuffersUnlocked(
        int sampleRateHz,
        int channels,
        double crossfeedCutoffHz,
        double crossfeedFeedDb,
        double crossfeedDelayMs,
        double limiterCeilingDb)
    {
        double cutoff = Math.Clamp(crossfeedCutoffHz, 450.0, 950.0);
        _crossAlphaLp = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sampleRateHz), 0.001, 0.999);
        double feedDb = Math.Clamp(crossfeedFeedDb, 1.5, 9.5);
        double rawFeed = Math.Pow(10.0, -feedDb / 20.0);
        double norm = 1.0 / Math.Sqrt(1.0 + rawFeed * rawFeed);
        _crossDirectGain = norm;
        _crossFeedGain = rawFeed * norm;
        _crossDelaySamples = Math.Clamp((int)Math.Round(Math.Clamp(crossfeedDelayMs, 0.15, 0.45) / 1000.0 * sampleRateHz), 1, 128);
        if (_crossDelayBufL.Length < _crossDelaySamples)
        {
            _crossDelayBufL = new double[_crossDelaySamples];
            _crossDelayBufR = new double[_crossDelaySamples];
        }
        Array.Clear(_crossDelayBufL);
        Array.Clear(_crossDelayBufR);
        _crossDelayPos = 0;
        _crossLpL = 0.0;
        _crossLpR = 0.0;

        _limiterDelaySamples = Math.Clamp((int)Math.Round(0.003 * sampleRateHz), 8, 1024);
        if (_limiterDelayBuf.Length != channels || _limiterDelayBuf[0].Length < _limiterDelaySamples)
        {
            _limiterDelayBuf = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                _limiterDelayBuf[c] = new double[_limiterDelaySamples];
            }
        }
        else
        {
            for (int c = 0; c < channels; c++) Array.Clear(_limiterDelayBuf[c]);
        }
        _limiterDelayPos = 0;
        _limiterEnvelope = 1.0;
        _limiterReleaseCoeff = Math.Exp(-1.0 / (0.08 * sampleRateHz));
        _limiterCeilingLinear = Math.Pow(10.0, Math.Clamp(limiterCeilingDb, -3.0, -0.05) / 20.0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool ReachedEndOfStream => _reachedEndOfStream;

    public void ResetEndOfStream() => _reachedEndOfStream = false;

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

    public bool IsCrossfeedActive
    {
        get { lock (_dspLock) return _crossfeedEnabled && WaveFormat.Channels == 2 && !_gainMatchedBypass; }
    }

    public bool IsGainMatchedBypass
    {
        get { lock (_dspLock) return _gainMatchedBypass; }
    }

    public bool IsLimiterActive
    {
        get { lock (_dspLock) return _limiterEnabled; }
    }

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
        double rmsShift = DesktopDspChainConfig.EstimateRmsLoudnessShiftDb(profile, WaveFormat.SampleRate);

        lock (_dspLock)
        {
            _lastProfile = profile;
            _lastReplayGainDb = replayGainDb;
            EffectivePreampDb = effectiveDb;
            _preampLinear = linear;
            _rmsShiftDb = rmsShift;
            _biquadNode.Configure(profile, WaveFormat.SampleRate, WaveFormat.Channels);
        }
    }

    public void SetReplayGainDb(double replayGainDb)
    {
        lock (_dspLock)
        {
            _lastReplayGainDb = replayGainDb;
            double effectiveDb = _lastProfile is not null
                ? _lastProfile.ComputeEffectivePreampDb(replayGainDb)
                : replayGainDb;
            EffectivePreampDb = effectiveDb;
            _preampLinear = Math.Pow(10.0, effectiveDb / 20.0);
        }
    }

    public void ConfigureExtendedDsp(
        bool gainMatchedBypass,
        double? rmsShiftDbOverride,
        bool crossfeedEnabled,
        double crossfeedCutoffHz,
        double crossfeedFeedDb,
        double crossfeedDelayMs,
        double balanceDb,
        bool monoFold,
        bool limiterEnabled,
        double limiterCeilingDb = -0.2)
    {
        int sr = Math.Max(WaveFormat.SampleRate, 8000);
        int ch = Math.Max(WaveFormat.Channels, 1);

        lock (_dspLock)
        {
            _gainMatchedBypass = gainMatchedBypass;
            if (rmsShiftDbOverride.HasValue)
            {
                _rmsShiftDb = rmsShiftDbOverride.Value;
            }
            bool crossParamsChanged =
                _crossfeedEnabled != crossfeedEnabled
                || Math.Abs(crossfeedCutoffHz - 700.0) > 1e-3
                || Math.Abs(crossfeedFeedDb - 4.5) > 1e-3
                || Math.Abs(_limiterCeilingDb - limiterCeilingDb) > 1e-3;

            _crossfeedEnabled = crossfeedEnabled;
            _balanceDb = Math.Clamp(balanceDb, -6.0, 6.0);
            _leftBalanceGain = _balanceDb > 0.0 ? Math.Pow(10.0, -_balanceDb / 20.0) : 1.0;
            _rightBalanceGain = _balanceDb < 0.0 ? Math.Pow(10.0, _balanceDb / 20.0) : 1.0;
            _monoFold = monoFold;
            _limiterEnabled = limiterEnabled;
            _limiterCeilingDb = limiterCeilingDb;

            if (crossParamsChanged || !crossfeedEnabled)
            {
                InitExtendedBuffersUnlocked(sr, ch, crossfeedCutoffHz, crossfeedFeedDb, crossfeedDelayMs, limiterCeilingDb);
            }
            else
            {
                double cutoff = Math.Clamp(crossfeedCutoffHz, 450.0, 950.0);
                _crossAlphaLp = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sr), 0.001, 0.999);
                double feedDb = Math.Clamp(crossfeedFeedDb, 1.5, 9.5);
                double rawFeed = Math.Pow(10.0, -feedDb / 20.0);
                double norm = 1.0 / Math.Sqrt(1.0 + rawFeed * rawFeed);
                _crossDirectGain = norm;
                _crossFeedGain = rawFeed * norm;
                _limiterCeilingLinear = Math.Pow(10.0, Math.Clamp(limiterCeilingDb, -3.0, -0.05) / 20.0);
            }
        }
    }

    public void ConfigureIso226(bool isEnabled, double referenceSplDb, double volumeAttenuationDb)
    {
        lock (_dspLock)
        {
            _iso226.Configure(isEnabled, referenceSplDb, volumeAttenuationDb);
        }
    }

    /// <summary>
    /// Master digital volume stage (applied after preamp/ReplayGain). Muted forces silence.
    /// </summary>
    public void ConfigureMasterVolume(double attenuationDb, bool muted)
    {
        double clamped = Math.Clamp(attenuationDb, -60.0, 0.0);
        double linear = muted ? 0.0 : (clamped >= -0.05 ? 1.0 : Math.Pow(10.0, clamped / 20.0));
        lock (_dspLock)
        {
            _masterLinear = linear;
        }
    }

    public double MasterLinearGain
    {
        get { lock (_dspLock) return _masterLinear; }
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
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
        {
            lock (_dspLock)
            {
                _firConvolver.Clear();
            }
            return;
        }

        var (left, right) = PartitionedFirConvolver.ReadImpulseArraysFromSofaOrWav(wavPath);
        lock (_dspLock)
        {
            _firConvolver.DryWetMix = dryWetMix;
            _firConvolver.LoadImpulse(left, right, normalizeEnergy: true, sourceLabel: wavPath);
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
        if (read <= 0)
        {
            _reachedEndOfStream = true;
            return read;
        }
        if (_bypass)
        {
            return read;
        }

        int channels = Math.Max(WaveFormat.Channels, 1);
        lock (_dspLock)
        {
            bool abBypass = _gainMatchedBypass;
            bool anyEq = !abBypass && _biquadNode.TotalSpecCount > 0;
            bool anyIso = !abBypass && _iso226.IsEnabled && _iso226.VolumeAttenuationDb < -0.5;
            bool anyFir = !abBypass && _firConvolver.IsEnabled && _firConvolver.LoadedTapCount > 0;
            bool anyVst3 = !abBypass && _vst3Bridge.ActiveSlotCount > 0 && _vst3Bridge.IsWorkerConnected;
            bool anyCrossfeed = !abBypass && _crossfeedEnabled && channels == 2;
            bool anyBalanceMono = !abBypass && (Math.Abs(_balanceDb) > 1e-4 || _monoFold);

            double gain = abBypass
                ? Math.Pow(10.0, Math.Clamp(_lastReplayGainDb + _rmsShiftDb, -24.0, 12.0) / 20.0)
                : _preampLinear;
            double master = _masterLinear;
            bool anyGain = Math.Abs(gain * master - 1.0) >= 1e-9;
            bool runLimiter = _limiterEnabled && (anyEq || anyGain || anyIso || anyCrossfeed || abBypass);

            if (!anyEq && !anyGain && !anyIso && !anyFir && !anyVst3 && !anyCrossfeed && !anyBalanceMono && !runLimiter)
            {
                return read;
            }

            if (!anyCrossfeed && !anyBalanceMono && !runLimiter)
            {
                for (int i = 0; i < read; i++)
                {
                    int ch = i % channels;
                    double sample = buffer[offset + i] * gain;
                    if (anyIso) sample = _iso226.ProcessSample(sample, ch);
                    if (anyEq) sample = _biquadNode.ProcessSample(sample, ch);
                    if (anyFir) sample = _firConvolver.ProcessSample(sample, ch);
                    if (anyVst3) sample = _vst3Bridge.ProcessSample(sample, ch);
                    sample *= master;
                    sample = _dither.ProcessSample(sample);
                    buffer[offset + i] = (float)sample;
                }
                return read;
            }

            if (_frameWork.Length < channels)
            {
                _frameWork = new double[channels];
            }
            int frameCount = read / channels;
            double[] frameWork = _frameWork;
            for (int frame = 0; frame < frameCount; frame++)
            {
                int baseIdx = offset + frame * channels;
                for (int c = 0; c < channels; c++)
                {
                    double sample = buffer[baseIdx + c] * gain;
                    if (anyIso) sample = _iso226.ProcessSample(sample, c);
                    if (anyEq) sample = _biquadNode.ProcessSample(sample, c);
                    if (anyFir) sample = _firConvolver.ProcessSample(sample, c);
                    if (anyVst3) sample = _vst3Bridge.ProcessSample(sample, c);
                    frameWork[c] = sample;
                }

                if (anyCrossfeed)
                {
                    double inL = frameWork[0];
                    double inR = frameWork[1];
                    _crossLpL += _crossAlphaLp * (inL - _crossLpL);
                    _crossLpR += _crossAlphaLp * (inR - _crossLpR);
                    double delayedR = _crossDelayBufR[_crossDelayPos];
                    double delayedL = _crossDelayBufL[_crossDelayPos];
                    _crossDelayBufL[_crossDelayPos] = _crossLpL;
                    _crossDelayBufR[_crossDelayPos] = _crossLpR;
                    _crossDelayPos = (_crossDelayPos + 1) % _crossDelaySamples;
                    frameWork[0] = inL * _crossDirectGain + delayedR * _crossFeedGain;
                    frameWork[1] = inR * _crossDirectGain + delayedL * _crossFeedGain;
                }

                if (anyBalanceMono)
                {
                    if (channels >= 2)
                    {
                        double l = frameWork[0] * _leftBalanceGain;
                        double r = frameWork[1] * _rightBalanceGain;
                        if (_monoFold)
                        {
                            double m = 0.5 * (l + r);
                            l = m;
                            r = m;
                        }
                        frameWork[0] = l;
                        frameWork[1] = r;
                    }
                    else
                    {
                        frameWork[0] *= _leftBalanceGain;
                    }
                }

                for (int c = 0; c < channels; c++)
                {
                    frameWork[c] *= master;
                }

                if (runLimiter)
                {
                    double futurePeak = 0.0;
                    for (int c = 0; c < channels; c++)
                    {
                        double s = frameWork[c];
                        double delayed = _limiterDelayBuf[c][_limiterDelayPos];
                        _limiterDelayBuf[c][_limiterDelayPos] = s;
                        double absS = Math.Abs(s);
                        if (absS > futurePeak) futurePeak = absS;
                        frameWork[c] = delayed;
                    }
                    _limiterDelayPos = (_limiterDelayPos + 1) % _limiterDelaySamples;

                    double instantPeak = futurePeak;
                    for (int c = 0; c < channels; c++)
                    {
                        double absD = Math.Abs(frameWork[c]);
                        if (absD > instantPeak) instantPeak = absD;
                    }
                    double targetGain = instantPeak > _limiterCeilingLinear
                        ? _limiterCeilingLinear / instantPeak
                        : 1.0;
                    _limiterEnvelope = targetGain < _limiterEnvelope
                        ? targetGain
                        : _limiterEnvelope * _limiterReleaseCoeff + targetGain * (1.0 - _limiterReleaseCoeff);

                    for (int c = 0; c < channels; c++)
                    {
                        double limited = frameWork[c] * _limiterEnvelope;
                        if (Math.Abs(limited) > _limiterCeilingLinear)
                        {
                            limited = _limiterCeilingLinear * Math.Tanh(limited / _limiterCeilingLinear);
                        }
                        buffer[baseIdx + c] = (float)_dither.ProcessSample(limited);
                    }
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                    {
                        buffer[baseIdx + c] = (float)_dither.ProcessSample(frameWork[c]);
                    }
                }
            }
        }

        return read;
    }
}
