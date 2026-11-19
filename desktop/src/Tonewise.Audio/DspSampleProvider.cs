using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// 64-bit floating-point DSP stage supporting ReplayGain, anti-clipping preamp,
/// ISO 226 dynamic equal-loudness, dual-channel L/R 64-band Biquad PEQ,
/// partitioned FIR convolution, ±1 LSB TPDF dithering, and bit-perfect bypass.
/// </summary>
public sealed class DspSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly Lock _dspLock = new();
    private readonly DualChannelBiquadNode _biquadNode = new();
    private readonly Iso226LoudnessNode _iso226;
    private readonly PartitionedFirConvolver _firConvolver;
    private readonly SandboxedVst3HostBridge _vst3Bridge = new();
    private readonly TpdfDitherProcessor _dither = new();

    private double _targetPreampLinear = 1.0;
    private double _currentPreampLinear = 1.0;
    private double _userMasterLinear = 1.0;
    private double _sleepFadeLinear = 1.0;
    private double _targetMasterLinear = 1.0;
    private double _currentMasterLinear = 1.0;
    private double _gainRampCoeff = 0.002;
    private bool _hasProcessedSamples;
    private EqualizerApoProfile? _lastProfile;
    private double _lastReplayGainDb;
    private volatile bool _bypass;
    private volatile bool _reachedEndOfStream;

    // Extended stereo acoustic stages (Gain-Matched A/B, Bauer Crossfeed, Balance/Mono, 3ms Lookahead Limiter)
    private bool _gainMatchedBypass;
    private double _rmsShiftDb;
    private double _targetGainMatchedBypassLinear = 1.0;
    private double _currentGainMatchedBypassLinear = 1.0;
    private bool _crossfeedEnabled;
    private double _lastCrossfeedCutoffHz = 700.0;
    private double _lastCrossfeedFeedDb = 4.5;
    private double _lastCrossfeedDelayMs = 0.28;
    private int _configuredSampleRateHz;
    private int _configuredChannels;
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
    private double[] _limiterPeakRing = new double[160];
    private double _limiterRunningMax;
    private int _limiterDelayPos;
    private double _limiterEnvelope = 1.0;
    private double _limiterAttackCoeff = 0.98;
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
        _configuredSampleRateHz = sampleRateHz;
        _configuredChannels = channels;
        _lastCrossfeedCutoffHz = crossfeedCutoffHz;
        _lastCrossfeedFeedDb = crossfeedFeedDb;
        _lastCrossfeedDelayMs = crossfeedDelayMs;

        double cutoff = Math.Clamp(crossfeedCutoffHz, 450.0, 950.0);
        _crossAlphaLp = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sampleRateHz), 0.001, 0.999);
        double feedDb = Math.Clamp(crossfeedFeedDb, 1.5, 9.5);
        double rawFeed = Math.Pow(10.0, -feedDb / 20.0);
        double norm = 1.0 / Math.Sqrt(1.0 + rawFeed * rawFeed);
        _crossDirectGain = norm;
        _crossFeedGain = rawFeed * norm;
        _crossDelaySamples = Math.Clamp((int)Math.Round(Math.Clamp(crossfeedDelayMs, 0.15, 0.45) / 1000.0 * sampleRateHz), 1, 256);
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

        _gainRampCoeff = 1.0 - Math.Exp(-1.0 / (0.012 * sampleRateHz));
        _limiterDelaySamples = Math.Clamp((int)Math.Round(0.003 * sampleRateHz), 8, 2048);
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
        if (_limiterPeakRing.Length < _limiterDelaySamples)
        {
            _limiterPeakRing = new double[_limiterDelaySamples];
        }
        else
        {
            Array.Clear(_limiterPeakRing);
        }
        _limiterRunningMax = 0.0;
        _limiterDelayPos = 0;
        _limiterEnvelope = 1.0;
        _limiterAttackCoeff = Math.Exp(-Math.Log(20.0) / Math.Max(_limiterDelaySamples, 1));
        _limiterReleaseCoeff = Math.Exp(-1.0 / (0.08 * sampleRateHz));
        _limiterCeilingLinear = Math.Pow(10.0, Math.Clamp(limiterCeilingDb, -3.0, -0.05) / 20.0);
    }

    private void UpdateGainMatchedBypassLinearUnlocked()
    {
        _targetGainMatchedBypassLinear = Math.Pow(10.0, Math.Clamp(_lastReplayGainDb + _rmsShiftDb, -24.0, 12.0) / 20.0);
        _currentGainMatchedBypassLinear = _targetGainMatchedBypassLinear;
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
            _targetPreampLinear = linear;
            if (!_hasProcessedSamples)
            {
                _currentPreampLinear = linear;
            }
            _rmsShiftDb = rmsShift;
            UpdateGainMatchedBypassLinearUnlocked();
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
            double linear = Math.Pow(10.0, effectiveDb / 20.0);
            _targetPreampLinear = linear;
            _currentPreampLinear = linear;
            UpdateGainMatchedBypassLinearUnlocked();
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
            UpdateGainMatchedBypassLinearUnlocked();
            bool formatChanged = _configuredSampleRateHz != sr || _configuredChannels != ch || _limiterDelayBuf.Length != ch;
            bool crossDelayChanged = Math.Abs(_lastCrossfeedDelayMs - crossfeedDelayMs) > 1e-4;
            bool crossTurnedOn = !_crossfeedEnabled && crossfeedEnabled;
            bool crossTurnedOff = _crossfeedEnabled && !crossfeedEnabled;

            _crossfeedEnabled = crossfeedEnabled;
            _balanceDb = Math.Clamp(balanceDb, -6.0, 6.0);
            _leftBalanceGain = _balanceDb > 0.0 ? Math.Pow(10.0, -_balanceDb / 20.0) : 1.0;
            _rightBalanceGain = _balanceDb < 0.0 ? Math.Pow(10.0, _balanceDb / 20.0) : 1.0;
            _monoFold = monoFold;
            _limiterEnabled = limiterEnabled;
            _limiterCeilingDb = limiterCeilingDb;

            if (formatChanged)
            {
                InitExtendedBuffersUnlocked(sr, ch, crossfeedCutoffHz, crossfeedFeedDb, crossfeedDelayMs, limiterCeilingDb);
            }
            else
            {
                _lastCrossfeedCutoffHz = crossfeedCutoffHz;
                _lastCrossfeedFeedDb = crossfeedFeedDb;
                _lastCrossfeedDelayMs = crossfeedDelayMs;

                double cutoff = Math.Clamp(crossfeedCutoffHz, 450.0, 950.0);
                _crossAlphaLp = Math.Clamp(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sr), 0.001, 0.999);
                double feedDb = Math.Clamp(crossfeedFeedDb, 1.5, 9.5);
                double rawFeed = Math.Pow(10.0, -feedDb / 20.0);
                double norm = 1.0 / Math.Sqrt(1.0 + rawFeed * rawFeed);
                _crossDirectGain = norm;
                _crossFeedGain = rawFeed * norm;

                if (crossDelayChanged || crossTurnedOn || crossTurnedOff)
                {
                    _crossDelaySamples = Math.Clamp((int)Math.Round(Math.Clamp(crossfeedDelayMs, 0.15, 0.45) / 1000.0 * sr), 1, 256);
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
                }

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
    /// Master digital volume stage (applied after preamp/ReplayGain). Muted or 0% forces exact silence.
    /// </summary>
    public void ConfigureMasterVolume(double attenuationDb, bool muted)
    {
        double clamped = Math.Clamp(attenuationDb, -80.0, 0.0);
        double linear = (muted || clamped <= -79.5)
            ? 0.0
            : (clamped >= -0.05 ? 1.0 : Math.Pow(10.0, clamped / 20.0));
        lock (_dspLock)
        {
            _userMasterLinear = linear;
            _targetMasterLinear = _userMasterLinear * _sleepFadeLinear;
            if (!_hasProcessedSamples)
            {
                _currentMasterLinear = _targetMasterLinear;
            }
        }
    }

    public void SetSleepFadeLinear(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        lock (_dspLock)
        {
            _sleepFadeLinear = clamped;
            _targetMasterLinear = _userMasterLinear * _sleepFadeLinear;
            if (!_hasProcessedSamples)
            {
                _currentMasterLinear = _targetMasterLinear;
            }
        }
    }

    public void Dispose() => _vst3Bridge.Dispose();

    public double MasterLinearGain
    {
        get { lock (_dspLock) return _targetMasterLinear; }
    }

    public void SetFirDryWetMix(double dryWetMix)
    {
        lock (_dspLock)
        {
            _firConvolver.DryWetMix = dryWetMix;
        }
    }

    public void ResetFilterStates()
    {
        lock (_dspLock)
        {
            _biquadNode.Reset();
            _iso226.Reset();
            _firConvolver.ResetState();
            Array.Clear(_crossDelayBufL);
            Array.Clear(_crossDelayBufR);
            _crossDelayPos = 0;
            _crossLpL = 0.0;
            _crossLpR = 0.0;
            for (int c = 0; c < _limiterDelayBuf.Length; c++)
            {
                Array.Clear(_limiterDelayBuf[c]);
            }
            Array.Clear(_limiterPeakRing);
            _limiterRunningMax = 0.0;
            _limiterDelayPos = 0;
            _limiterEnvelope = 1.0;
            _currentPreampLinear = _targetPreampLinear;
            _currentMasterLinear = _targetMasterLinear;
            _currentGainMatchedBypassLinear = _targetGainMatchedBypassLinear;
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
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
        {
            lock (_dspLock)
            {
                _firConvolver.Clear();
            }
            return;
        }

        var (left, right) = PartitionedFirConvolver.ReadImpulseArraysFromSofaOrWav(wavPath, WaveFormat.SampleRate);
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

    private bool IsIdentityLocked(int channels)
    {
        bool abBypass = _gainMatchedBypass;
        bool anyEq = !abBypass && _biquadNode.TotalSpecCount > 0;
        bool anyIso = !abBypass && _iso226.IsEnabled && _iso226.VolumeAttenuationDb < -0.5;
        bool anyFir = !abBypass && _firConvolver.IsEnabled && _firConvolver.LoadedTapCount > 0;
        bool anyVst3 = !abBypass && _vst3Bridge.ActiveSlotCount > 0 && _vst3Bridge.IsWorkerConnected;
        bool anyCrossfeed = !abBypass && _crossfeedEnabled && channels == 2;
        bool anyBalanceMono = !abBypass && (Math.Abs(_balanceDb) > 1e-4 || _monoFold);

        double targetGain = abBypass ? _targetGainMatchedBypassLinear : _targetPreampLinear;
        double currentGain = abBypass ? _currentGainMatchedBypassLinear : _currentPreampLinear;
        bool anyGain = Math.Abs(targetGain * _targetMasterLinear - 1.0) >= 1e-9
            || Math.Abs(currentGain * _currentMasterLinear - 1.0) >= 1e-9;
        bool runLimiter = _limiterEnabled && (anyEq || anyGain || anyIso || anyFir || anyVst3 || anyCrossfeed || abBypass);

        return !anyEq && !anyGain && !anyIso && !anyFir && !anyVst3 && !anyCrossfeed && !anyBalanceMono && !runLimiter;
    }

    public bool IsIdentityPassthrough
    {
        get
        {
            if (_bypass) return true;
            lock (_dspLock)
            {
                return IsIdentityLocked(Math.Max(WaveFormat.Channels, 1));
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_source is GaplessSampleProvider gapless)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int seg = gapless.ReadSegment(buffer, offset + totalRead, count - totalRead);
                if (seg <= 0)
                {
                    if (totalRead == 0)
                    {
                        _reachedEndOfStream = true;
                    }
                    break;
                }
                if (!_bypass)
                {
                    ProcessSegment(buffer, offset + totalRead, seg);
                }
                totalRead += seg;
            }
            return totalRead;
        }

        int read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            _reachedEndOfStream = true;
            return read;
        }
        if (!_bypass)
        {
            ProcessSegment(buffer, offset, read);
        }
        return read;
    }

    private void ProcessSegment(float[] buffer, int offset, int read)
    {
        int channels = Math.Max(WaveFormat.Channels, 1);
        lock (_dspLock)
        {
            if (IsIdentityLocked(channels))
            {
                return;
            }
            bool abBypass = _gainMatchedBypass;
            bool anyEq = !abBypass && _biquadNode.TotalSpecCount > 0;
            bool anyIso = !abBypass && _iso226.IsEnabled && _iso226.VolumeAttenuationDb < -0.5;
            bool anyFir = !abBypass && _firConvolver.IsEnabled && _firConvolver.LoadedTapCount > 0;
            bool anyVst3 = !abBypass && _vst3Bridge.ActiveSlotCount > 0 && _vst3Bridge.IsWorkerConnected;
            bool anyCrossfeed = !abBypass && _crossfeedEnabled && channels == 2;
            bool anyBalanceMono = !abBypass && (Math.Abs(_balanceDb) > 1e-4 || _monoFold);

            double targetGain = abBypass ? _targetGainMatchedBypassLinear : _targetPreampLinear;
            double targetMaster = _targetMasterLinear;
            bool anyGain = Math.Abs(targetGain * targetMaster - 1.0) >= 1e-9
                || Math.Abs((abBypass ? _currentGainMatchedBypassLinear : _currentPreampLinear) * _currentMasterLinear - 1.0) >= 1e-9;
            bool runLimiter = _limiterEnabled && (anyEq || anyGain || anyIso || anyFir || anyVst3 || anyCrossfeed || abBypass);
            _hasProcessedSamples = true;

            if (!anyCrossfeed && !anyBalanceMono && !runLimiter)
            {
                for (int i = 0; i < read; i++)
                {
                    int ch = i % channels;
                    if (ch == 0)
                    {
                        if (abBypass)
                            _currentGainMatchedBypassLinear += (_targetGainMatchedBypassLinear - _currentGainMatchedBypassLinear) * _gainRampCoeff;
                        else
                            _currentPreampLinear += (_targetPreampLinear - _currentPreampLinear) * _gainRampCoeff;
                        _currentMasterLinear += (targetMaster - _currentMasterLinear) * _gainRampCoeff;
                    }
                    double g = abBypass ? _currentGainMatchedBypassLinear : _currentPreampLinear;
                    double sample = buffer[offset + i] * g;
                    if (anyIso) sample = _iso226.ProcessSample(sample, ch);
                    if (anyEq) sample = _biquadNode.ProcessSample(sample, ch);
                    if (anyFir) sample = _firConvolver.ProcessSample(sample, ch);
                    if (anyVst3) sample = _vst3Bridge.ProcessSample(sample, ch);
                    sample *= _currentMasterLinear;
                    sample = _dither.ProcessSample(sample);
                    buffer[offset + i] = (float)sample;
                }
                return;
            }

            if (_frameWork.Length < channels)
            {
                _frameWork = new double[channels];
            }
            int frameCount = read / channels;
            double[] frameWork = _frameWork;
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (abBypass)
                    _currentGainMatchedBypassLinear += (_targetGainMatchedBypassLinear - _currentGainMatchedBypassLinear) * _gainRampCoeff;
                else
                    _currentPreampLinear += (_targetPreampLinear - _currentPreampLinear) * _gainRampCoeff;
                _currentMasterLinear += (targetMaster - _currentMasterLinear) * _gainRampCoeff;

                double g = abBypass ? _currentGainMatchedBypassLinear : _currentPreampLinear;
                int baseIdx = offset + frame * channels;
                for (int c = 0; c < channels; c++)
                {
                    double sample = buffer[baseIdx + c] * g;
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
                    frameWork[c] *= _currentMasterLinear;
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
                    double evicted = _limiterPeakRing[_limiterDelayPos];
                    _limiterPeakRing[_limiterDelayPos] = futurePeak;
                    _limiterDelayPos = (_limiterDelayPos + 1) % _limiterDelaySamples;

                    double windowPeak;
                    if (futurePeak >= _limiterRunningMax)
                    {
                        _limiterRunningMax = futurePeak;
                        windowPeak = futurePeak;
                    }
                    else if (evicted >= _limiterRunningMax - 1e-12)
                    {
                        double m = 0.0;
                        for (int k = 0; k < _limiterDelaySamples; k++)
                        {
                            if (_limiterPeakRing[k] > m) m = _limiterPeakRing[k];
                        }
                        _limiterRunningMax = m;
                        windowPeak = m;
                    }
                    else
                    {
                        windowPeak = _limiterRunningMax;
                    }

                    double delayedPeak = 0.0;
                    for (int c = 0; c < channels; c++)
                    {
                        double absD = Math.Abs(frameWork[c]);
                        if (absD > delayedPeak) delayedPeak = absD;
                    }
                    double maxPeak = Math.Max(windowPeak, delayedPeak);
                    double limiterTargetGain = maxPeak > _limiterCeilingLinear
                        ? _limiterCeilingLinear / maxPeak
                        : 1.0;
                    _limiterEnvelope = limiterTargetGain < _limiterEnvelope
                        ? _limiterEnvelope * _limiterAttackCoeff + limiterTargetGain * (1.0 - _limiterAttackCoeff)
                        : _limiterEnvelope * _limiterReleaseCoeff + limiterTargetGain * (1.0 - _limiterReleaseCoeff);
                    if (delayedPeak > _limiterCeilingLinear)
                    {
                        double safeGain = _limiterCeilingLinear / delayedPeak;
                        if (_limiterEnvelope > safeGain) _limiterEnvelope = safeGain;
                    }

                    for (int c = 0; c < channels; c++)
                    {
                        double limited = SoftLimitSample(frameWork[c] * _limiterEnvelope, _limiterCeilingLinear);
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
    }

    public static double SoftLimitSample(double normalized, double ceiling)
    {
        double c = Math.Clamp(ceiling, 0.1, 1.0);
        double knee = c * 0.85;
        double absX = Math.Abs(normalized);
        if (absX <= knee) return normalized;
        double headroom = c - knee;
        double shaped = knee + headroom * Math.Tanh((absX - knee) / headroom);
        return normalized >= 0.0 ? shaped : -shaped;
    }
}
