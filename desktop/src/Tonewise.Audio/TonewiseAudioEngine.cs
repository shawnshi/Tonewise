using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Tonewise.Audio;

public sealed record AudioPlaybackInterruption(
    string FilePath,
    TimeSpan Position,
    TimeSpan Duration,
    bool WasExclusive,
    int HResult,
    string Reason);

public sealed record DesktopSignalPathState(
    string OutputDeviceName,
    string ShareModeLabel,
    int SampleRateHz,
    int BitsPerSample,
    bool IsExclusive,
    bool DspBypassed,
    int BiquadCount,
    double EffectivePreampDb,
    bool Iso226Active = false,
    int FirTapCount = 0,
    bool TpdfDitherActive = false,
    bool HasSystemApoConflict = false,
    string? SystemApoWarning = null,
    string SignalPathBadge = "Idle — Standby",
    bool CrossfeedActive = false,
    bool GainMatchedBypass = false,
    bool LimiterActive = false
);

public sealed class TonewiseAudioEngine : IDisposable
{
    private readonly Lock _engineLock = new();
    private readonly IPlaybackPowerGuard _powerGuard;
    private readonly bool _ownsPowerGuard;
    private IWavePlayer? _output;
    private WaveStream? _sourceWaveStream;
    private ISampleProvider? _sourceSamples;
    private GaplessSampleProvider? _gapless;
    private IDisposable? _sourceDisposable;
    private DspSampleProvider? _dsp;
    private MMDevice? _device;
    private string? _cachedDeviceName;
    private string? _cachedDeviceId;
    private ApoConflictStatus _cachedApoStatus = new(false, null);
    private int _playbackEndedSignaled;

    private EqualizerApoProfile? _activeProfile;
    private DesktopDspChainConfig _extendedDsp = new();
    private double _activeReplayGainDb;
    private bool _preferExclusive = true;
    private string? _selectedDeviceId;
    private string? _asioDriverName;
    private bool _iso226Enabled;
    private double _iso226RefSplDb = 80.0;
    private double _volumeAttenuationDb;
    private string? _firWavPath;
    private volatile bool _hasFirWavFile;
    private double[]? _firLeftIr;
    private double[]? _firRightIr;
    private string? _firLabel;
    private double _firDryWetMix = 1.0;
    private bool _tpdfDitherEnabled = true;
    private volatile bool _isDsdSource;
    private bool _isAsioActive;
    private volatile bool _pausePending;
    private volatile bool _pauseRequested;
    private long _transportOpGen;
    private int _outputLatencyMs = 100;
    private Task? _pendingErrorTeardown;
    private volatile BypassSnapshot _bypassSnapshot = new(false, 0.0, false, false, false, false);

    public TonewiseAudioEngine() : this(null)
    {
    }

    internal TonewiseAudioEngine(IPlaybackPowerGuard? powerGuard)
    {
        _ownsPowerGuard = powerGuard is null;
        _powerGuard = powerGuard ?? new WindowsPlaybackPowerGuard("Tonewise Audio Playback");
    }

    private sealed record BypassSnapshot(
        bool HasEq,
        double ProfilePreampDb,
        bool HasIso,
        bool HasVolume,
        bool HasFir,
        bool HasExtended);

    public event Action<DesktopSignalPathState>? SignalPathChanged;
    public event Action? PlaybackEnded;
    public event Action<string>? GaplessTrackAdvanced;
    public event Action<string>? PlaybackError;
    public event Action<AudioPlaybackInterruption>? PlaybackInterrupted;

    public void ReleasePowerKeepAlive() => _powerGuard.ReleaseImmediate();

    public string? CurrentFilePath { get; private set; }
    public bool IsPlaying
    {
        get
        {
            lock (_engineLock)
            {
                return !_pausePending && _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public EqualizerApoProfile? ActiveProfile
    {
        get
        {
            lock (_engineLock)
            {
                return _activeProfile;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_engineLock)
            {
                try
                {
                    if (_gapless is not null) return _gapless.Position;
                    if (_sourceWaveStream is not null) return _sourceWaveStream.CurrentTime;
                    return _sourceSamples switch
                    {
                        AudioFileReader afr => afr.CurrentTime,
                        DsdStreamReader dsd => dsd.CurrentTime,
                        _ => TimeSpan.Zero,
                    };
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_engineLock)
            {
                try
                {
                    if (_gapless is not null) return _gapless.Duration;
                    if (_sourceWaveStream is not null) return _sourceWaveStream.TotalTime;
                    return _sourceSamples switch
                    {
                        AudioFileReader afr => afr.TotalTime,
                        DsdStreamReader dsd => dsd.TotalTime,
                        _ => TimeSpan.Zero,
                    };
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }
    }

    public string SignalPathStatus => CurrentSignalPath.SignalPathBadge;

    public double VolumeAttenuationDb
    {
        get
        {
            lock (_engineLock)
            {
                return _volumeAttenuationDb;
            }
        }
        set
        {
            DesktopSignalPathState? changed;
            lock (_engineLock)
            {
                _volumeAttenuationDb = Math.Clamp(value, -80.0, 0.0);
                changed = ApplyRuntimeDspConfigLocked();
            }
            if (changed is not null) SignalPathChanged?.Invoke(changed);
        }
    }

    private bool _isMuted;
    private double _sleepFadeLinear = 1.0;

    public void SetSleepFadeLinear(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        lock (_engineLock)
        {
            _sleepFadeLinear = clamped;
            _dsp?.SetSleepFadeLinear(clamped);
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_engineLock)
            {
                return _isMuted;
            }
        }
        set
        {
            DesktopSignalPathState? changed;
            lock (_engineLock)
            {
                if (_isMuted == value) return;
                _isMuted = value;
                changed = ApplyRuntimeDspConfigLocked();
            }
            if (changed is not null) SignalPathChanged?.Invoke(changed);
        }
    }

    public DesktopSignalPathState CurrentSignalPath { get; private set; } =
        new("System Default", "Idle — Standby", 0, 0, false, false, 0, 0.0);

    public void SetPreferences(bool preferExclusive, string? deviceId, EqualizerApoProfile? profile, double replayGainDb)
    {
        DesktopSignalPathState? changed;
        lock (_engineLock)
        {
            _preferExclusive = preferExclusive;
            _selectedDeviceId = deviceId;
            _activeProfile = profile;
            _activeReplayGainDb = replayGainDb;

            bool reloadFir = false;
            if (profile is not null)
            {
                if (profile.EnableIso226)
                {
                    _iso226Enabled = true;
                    _iso226RefSplDb = profile.ReferenceSplDb;
                }
                if (!string.IsNullOrWhiteSpace(profile.FirImpulsePath) && File.Exists(profile.FirImpulsePath))
                {
                    reloadFir = !string.Equals(_firWavPath, profile.FirImpulsePath, StringComparison.OrdinalIgnoreCase);
                    _firWavPath = profile.FirImpulsePath;
                    _hasFirWavFile = true;
                    _firLeftIr = null;
                    _firRightIr = null;
                }
            }

            changed = ApplyRuntimeDspConfigLocked(reloadFir);
        }
        if (changed is not null) SignalPathChanged?.Invoke(changed);
    }

    public void SetAcousticPipeline(
        bool iso226Enabled,
        double referenceSplDb,
        double volumeAttenuationDb,
        bool tpdfDitherEnabled,
        double firDryWetMix = 1.0)
    {
        DesktopSignalPathState? changed;
        lock (_engineLock)
        {
            _iso226Enabled = iso226Enabled;
            _iso226RefSplDb = Math.Clamp(referenceSplDb, 70.0, 90.0);
            _volumeAttenuationDb = Math.Clamp(volumeAttenuationDb, -80.0, 0.0);
            _tpdfDitherEnabled = tpdfDitherEnabled;
            _firDryWetMix = Math.Clamp(firDryWetMix, 0.0, 1.0);
            changed = ApplyRuntimeDspConfigLocked();
        }
        if (changed is not null) SignalPathChanged?.Invoke(changed);
    }

    public void SetExtendedDsp(DesktopDspChainConfig config)
    {
        DesktopSignalPathState? changed;
        lock (_engineLock)
        {
            _extendedDsp = config ?? new DesktopDspChainConfig();
            changed = ApplyRuntimeDspConfigLocked();
        }
        if (changed is not null) SignalPathChanged?.Invoke(changed);
    }

    public void SetFirImpulseFile(string? wavPath, double dryWetMix = 1.0)
    {
        DesktopSignalPathState? changed;
        lock (_engineLock)
        {
            _firWavPath = wavPath;
            _hasFirWavFile = !string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath);
            _firLeftIr = null;
            _firRightIr = null;
            _firDryWetMix = Math.Clamp(dryWetMix, 0.0, 1.0);
            changed = ApplyRuntimeDspConfigLocked(reloadFir: true);
        }
        if (changed is not null) SignalPathChanged?.Invoke(changed);
    }

    public void SetFirImpulseMemory(double[]? leftIr, double[]? rightIr, double dryWetMix = 1.0, string? label = null)
    {
        DesktopSignalPathState? changed;
        lock (_engineLock)
        {
            _firWavPath = null;
            _hasFirWavFile = false;
            _firLeftIr = leftIr;
            _firRightIr = rightIr;
            _firLabel = label;
            _firDryWetMix = Math.Clamp(dryWetMix, 0.0, 1.0);
            changed = ApplyRuntimeDspConfigLocked(reloadFir: true);
        }
        if (changed is not null) SignalPathChanged?.Invoke(changed);
    }

    public string? TryAutoLoadDeviceDspProfile(string musicRootPath, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(musicRootPath) || string.IsNullOrWhiteSpace(deviceName))
            return null;

        string profilesDir = Path.Combine(musicRootPath, ".tonewise", "dsp_profiles");
        if (!Directory.Exists(profilesDir))
            return null;

        foreach (string txtPath in Directory.EnumerateFiles(profilesDir, "*.txt"))
        {
            string raw = File.ReadAllText(txtPath);
            var parsed = EqualizerApoProfile.Parse(raw);
            string stem = Path.GetFileNameWithoutExtension(txtPath);

            bool deviceMatches =
                (!string.IsNullOrWhiteSpace(parsed.BoundDeviceName) &&
                 deviceName.Contains(parsed.BoundDeviceName, StringComparison.OrdinalIgnoreCase))
                || deviceName.Contains(stem, StringComparison.OrdinalIgnoreCase);

            if (!deviceMatches) continue;

            if (!string.IsNullOrWhiteSpace(parsed.FirImpulsePath) && !Path.IsPathRooted(parsed.FirImpulsePath))
            {
                string resolvedFir = Path.Combine(profilesDir, parsed.FirImpulsePath);
                parsed = parsed with { FirImpulsePath = resolvedFir };
            }

            SetPreferences(_preferExclusive, _selectedDeviceId, parsed, _activeReplayGainDb);
            return txtPath;
        }

        return null;
    }

    public void PlayFile(string filePath, double initialSeekFraction = 0.0, bool startPaused = false, TimeSpan? initialPosition = null, bool allowExclusive = true)
    {
        DetachedPlaybackHandles oldHandles;
        Task? pendingTeardown;
        lock (_engineLock)
        {
            oldHandles = DetachPlaybackHandlesLocked();
            pendingTeardown = _pendingErrorTeardown;
            _pendingErrorTeardown = null;
        }
        DisposeDetachedHandles(oldHandles);
        if (pendingTeardown is not null)
        {
            try { pendingTeardown.Wait(500); } catch { }
        }

        DesktopSignalPathState? signalSnapshotToNotify = null;
        DetachedPlaybackHandles failedHandles = default;
        ExceptionDispatchInfo? capturedException = null;
        bool shouldAcquirePower = false;
        bool shouldReleasePower = false;

        lock (_engineLock)
        {
            var stale = DetachPlaybackHandlesLocked();
            if (stale.HasAny)
            {
                // Dispose asynchronously outside _engineLock so PlaybackStopped cannot deadlock
                _ = ThreadPool.QueueUserWorkItem(_ => DisposeDetachedHandles(stale));
            }
            Interlocked.Exchange(ref _playbackEndedSignaled, 0);
            _pausePending = false;
            _pauseRequested = startPaused;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            _isDsdSource = ext is ".dsf" or ".dff";

            WaveStream? sourceWaveStream = null;
            ISampleProvider? sourceSamples = null;
            DecodeReadAheadSampleProvider? readAhead = null;
            GaplessSampleProvider? gapless = null;
            IDisposable? sourceDisposable = null;
            DspSampleProvider? dsp = null;
            IWavePlayer? player = null;
            MMDevice? device = null;
            string? resolvedDeviceName = null;
            string? resolvedDeviceId = null;
            ApoConflictStatus resolvedApoStatus = new(false, null);
            bool exclusiveOk = false;
            bool asioOk = false;

            try
            {
                if (_isDsdSource)
                {
                    var dsd = new DsdStreamReader(ToExtendedPathIfLong(filePath));
                    sourceSamples = dsd;
                    sourceDisposable = dsd;
                }
                else
                {
                    (sourceWaveStream, sourceSamples, sourceDisposable) = OpenPcmSourceWithLongPathFallback(filePath);
                }

                readAhead = new DecodeReadAheadSampleProvider(sourceWaveStream, sourceSamples, sourceDisposable);
                sourceDisposable = readAhead;

                gapless = new GaplessSampleProvider(
                    filePath,
                    sourceWaveStream,
                    readAhead,
                    readAhead,
                    onTransitionSync: (newPath, transition) =>
                    {
                        bool nextIsDsd = Path.GetExtension(newPath).ToLowerInvariant() is ".dsf" or ".dff";
                        double newRgDb = transition.ReplayGainDb;
                        CurrentFilePath = newPath;
                        _isDsdSource = nextIsDsd;
                        _activeReplayGainDb = newRgDb;
                        bool dspChanged = false;
                        if (transition.HasProfileOverride)
                        {
                            _activeProfile = transition.ComposedProfile;
                            dspChanged = true;
                        }
                        if (transition.CrossfeedEnabled.HasValue && _extendedDsp.CrossfeedEnabled != transition.CrossfeedEnabled.Value)
                        {
                            _extendedDsp = _extendedDsp with { CrossfeedEnabled = transition.CrossfeedEnabled.Value };
                            dspChanged = true;
                        }
                        if (dspChanged && dsp is not null)
                        {
                            dsp.Configure(_activeProfile, newRgDb);
                            bool extActive = _extendedDsp.Enabled;
                            bool firLoaded = _hasFirWavFile || (_firLeftIr is not null && _firRightIr is not null);
                            bool hasEq = _activeProfile is not null && _activeProfile.Filters.Any(HasNonIdentityFilter);
                            dsp.ConfigureExtendedDsp(
                                gainMatchedBypass: extActive && _extendedDsp.GainMatchedBypass,
                                rmsShiftDbOverride: null,
                                crossfeedEnabled: extActive && _extendedDsp.CrossfeedEnabled,
                                crossfeedCutoffHz: _extendedDsp.CrossfeedCutoffHz,
                                crossfeedFeedDb: _extendedDsp.CrossfeedFeedDb,
                                crossfeedDelayMs: _extendedDsp.CrossfeedDelayMs,
                                balanceDb: extActive ? _extendedDsp.BalanceDb : 0.0,
                                monoFold: extActive && _extendedDsp.MonoFold,
                                limiterEnabled: _extendedDsp.LimiterEnabled && (extActive || hasEq || firLoaded || Math.Abs(newRgDb) > 1e-4),
                                limiterCeilingDb: _extendedDsp.LimiterCeilingDb);
                            UpdateBypassSnapshotLocked();
                        }
                        else
                        {
                            dsp?.SetReplayGainDb(newRgDb);
                        }
                        if (dsp is not null)
                        {
                            dsp.IsBypassed = ShouldBypassDspFromSnapshot(_bypassSnapshot, exclusiveOk, nextIsDsd, newRgDb);
                        }
                    });
                gapless.TrackTransitioned += nextPath =>
                {
                    try
                    {
                        DesktopSignalPathState? transitionedSnap = null;
                        lock (_engineLock)
                        {
                            if (ReferenceEquals(_gapless, gapless))
                            {
                                transitionedSnap = UpdateSignalPathSnapshotLocked(exclusiveOk);
                            }
                        }
                        if (transitionedSnap is not null)
                        {
                            SignalPathChanged?.Invoke(transitionedSnap);
                        }
                        GaplessTrackAdvanced?.Invoke(nextPath);
                    }
                    catch
                    {
                        // Guard background ThreadPool transition notifications from terminating host process
                    }
                };
                sourceDisposable = gapless;
                dsp = new DspSampleProvider(gapless);

                ConfigureDspLocked(dsp, reloadFir: true);

                _asioDriverName = null;

                if (allowExclusive
                    && !string.IsNullOrWhiteSpace(_selectedDeviceId)
                    && _selectedDeviceId.StartsWith(AudioDeviceRouter.AsioIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string driver = _selectedDeviceId[AudioDeviceRouter.AsioIdPrefix.Length..];
                    AsioOut? asio = null;
                    try
                    {
                        asio = new AsioOut(driver);
                        asio.Init(dsp.ToWaveProvider());
                        player = asio;
                        asio = null;
                        asioOk = true;
                        exclusiveOk = true;
                        _asioDriverName = driver;
                    }
                    catch
                    {
                        try { asio?.Dispose(); } catch { }
                        player?.Dispose();
                        player = null;
                    }
                }

                if (player is null)
                {
                    device = AudioDeviceRouter.ResolveDevice(_selectedDeviceId);
                    if (device is not null)
                    {
                        try { resolvedDeviceName = device.FriendlyName; } catch { }
                        try { resolvedDeviceId = device.ID; } catch { }
                    }
                    if (allowExclusive && device is not null && _preferExclusive)
                    {
                        WasapiOut? wasapi = null;
                        try
                        {
                            wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 120);
                            _ = WasapiMediaCategoryHelper.TryConfigureMediaCategory(wasapi);
                            wasapi.Init(dsp);
                            player = wasapi;
                            wasapi = null;
                            exclusiveOk = true;
                        }
                        catch
                        {
                            try { wasapi?.Dispose(); } catch { }
                            player?.Dispose();
                            player = null;
                        }

                        if (player is null)
                        {
                            WasapiOut? exclusiveFallback = null;
                            try
                            {
                                exclusiveFallback = new WasapiOut(device, AudioClientShareMode.Exclusive, false, 150);
                                _ = WasapiMediaCategoryHelper.TryConfigureMediaCategory(exclusiveFallback);
                                exclusiveFallback.Init(dsp);
                                player = exclusiveFallback;
                                exclusiveFallback = null;
                                exclusiveOk = true;
                            }
                            catch
                            {
                                try { exclusiveFallback?.Dispose(); } catch { }
                                player?.Dispose();
                                player = null;
                            }
                        }
                    }

                    if (player is null)
                    {
                        WasapiOut? shared = null;
                        try
                        {
                            shared = device is not null
                                ? new WasapiOut(device, AudioClientShareMode.Shared, false, 150)
                                : new WasapiOut(AudioClientShareMode.Shared, 150);
                            _ = WasapiMediaCategoryHelper.TryConfigureMediaCategory(shared);
                            shared.Init(dsp);
                            player = shared;
                            shared = null;
                        }
                        catch
                        {
                            try { shared?.Dispose(); } catch { }
                            throw;
                        }
                    }
                }

                resolvedApoStatus = asioOk
                    ? new ApoConflictStatus(false, null)
                    : SystemApoConflictDetector.CheckEndpointConflict(
                        resolvedDeviceId ?? _selectedDeviceId,
                        exclusiveOk ? AudioOutputMode.WasapiExclusive : AudioOutputMode.WasapiShared);

                _isAsioActive = asioOk;
                _outputLatencyMs = asioOk ? 40 : (exclusiveOk ? 120 : 150);
                dsp.IsBypassed = ShouldBypassDspLocked(exclusiveOk);

                var capturedSource = sourceSamples;
                var capturedWaveStream = sourceWaveStream;
                var capturedGapless = gapless;
                var capturedDsp = dsp;
                var capturedPlayer = player;
                player.PlaybackStopped += (_, args) =>
                {
                    try
                    {
                        bool isActiveOutput;
                        bool finished = false;
                        bool raiseError = false;
                        string? errorMsg = null;
                        AudioPlaybackInterruption? interruption = null;
                        DetachedPlaybackHandles errorDetach = default;
                        DesktopSignalPathState? standbySnap = null;
                        lock (_engineLock)
                        {
                            isActiveOutput = ReferenceEquals(_output, capturedPlayer);
                            if (isActiveOutput)
                            {
                                if (args.Exception is null)
                                {
                                    try
                                    {
                                        finished = capturedDsp.ReachedEndOfStream
                                            || (capturedGapless is not null
                                                ? (capturedGapless.ReachedEndOfStream
                                                   || (capturedGapless.StagedNextPath is null
                                                       && capturedGapless.Duration.TotalSeconds > 0
                                                       && capturedGapless.Position >= capturedGapless.Duration - TimeSpan.FromMilliseconds(500)))
                                                : (capturedWaveStream is { TotalTime.TotalSeconds: > 0 } ws
                                                    ? ws.CurrentTime >= ws.TotalTime - TimeSpan.FromMilliseconds(500)
                                                    : capturedSource is DsdStreamReader dsd
                                                      && dsd.TotalTime.TotalSeconds > 0
                                                      && dsd.CurrentTime >= dsd.TotalTime - TimeSpan.FromMilliseconds(500)));
                                    }
                                    catch
                                    {
                                        finished = capturedDsp.ReachedEndOfStream;
                                    }
                                }

                                bool unexpectedStop = args.Exception is not null
                                    || (!finished && !_pausePending && !_pauseRequested && capturedPlayer.PlaybackState == PlaybackState.Stopped);

                                if (unexpectedStop)
                                {
                                    string interruptedPath = CurrentFilePath ?? "";
                                    TimeSpan interruptedPos = CapturePositionSafely(capturedGapless, capturedWaveStream, capturedSource);
                                    TimeSpan interruptedDur = CaptureDurationSafely(capturedGapless, capturedWaveStream, capturedSource);
                                    bool wasExclusive = CurrentSignalPath.IsExclusive;
                                    int hresult = (args.Exception as COMException)?.HResult ?? args.Exception?.HResult ?? 0;
                                    errorMsg = args.Exception?.Message ?? "音频输出流意外中断";
                                    interruption = new AudioPlaybackInterruption(
                                        interruptedPath,
                                        interruptedPos,
                                        interruptedDur,
                                        wasExclusive,
                                        hresult,
                                        errorMsg);
                                    errorDetach = DetachPlaybackHandlesLocked();
                                    standbySnap = ResetSignalPathToStandbyLocked();
                                    raiseError = true;
                                }
                            }
                        }
                        if (errorDetach.HasAny)
                        {
                            // Never synchronously Dispose() WASAPI/ASIO output on the PlaybackStopped callback thread:
                            // WasapiOut raises PlaybackStopped directly on PlayThread(), so Stop()/Dispose() here calls
                            // Thread.Join() on itself and permanently deadlocks device ownership (AUDCLNT_E_DEVICE_IN_USE).
                            var handlesToDispose = errorDetach;
                            Task teardownTask = Task.Run(() => DisposeDetachedHandles(handlesToDispose));
                            lock (_engineLock)
                            {
                                _pendingErrorTeardown = teardownTask;
                            }
                        }
                        if (standbySnap is not null)
                        {
                            SignalPathChanged?.Invoke(standbySnap);
                        }
                        if (raiseError)
                        {
                            if (!string.IsNullOrWhiteSpace(interruption?.FilePath))
                            {
                                _powerGuard.ScheduleDeferredRelease(TimeSpan.FromSeconds(8));
                            }
                            else
                            {
                                _powerGuard.ReleaseImmediate();
                            }
                            if (interruption is not null)
                            {
                                PlaybackInterrupted?.Invoke(interruption);
                            }
                            PlaybackError?.Invoke(errorMsg ?? "Audio output device error");
                            return;
                        }
                        if (isActiveOutput && args.Exception is null && finished && Interlocked.CompareExchange(ref _playbackEndedSignaled, 1, 0) == 0)
                        {
                            _powerGuard.ScheduleDeferredRelease(TimeSpan.FromSeconds(5));
                            PlaybackEnded?.Invoke();
                        }
                    }
                    catch
                    {
                        // Guard audio callback thread against unhandled subscriber errors
                    }
                };

                if (initialPosition.HasValue && initialPosition.Value > TimeSpan.Zero && gapless.Duration.TotalSeconds > 0)
                {
                    double frac = Math.Clamp(initialPosition.Value.TotalSeconds / gapless.Duration.TotalSeconds, 0.0, 0.999);
                    gapless.Seek(frac);
                }
                else if (initialSeekFraction > 0.0)
                {
                    gapless.Seek(Math.Clamp(initialSeekFraction, 0.0, 0.999));
                }

                CurrentFilePath = filePath;
                _sourceWaveStream = sourceWaveStream;
                _sourceSamples = sourceSamples;
                _gapless = gapless;
                _sourceDisposable = sourceDisposable;
                _dsp = dsp;
                _device = device;
                _cachedDeviceName = resolvedDeviceName;
                _cachedDeviceId = resolvedDeviceId;
                _cachedApoStatus = resolvedApoStatus;
                _output = player;

                signalSnapshotToNotify = UpdateSignalPathSnapshotLocked(exclusiveOk);
                if (!startPaused)
                {
                    dsp.BeginTransportFadeIn(durationMs: 10.0, startFromZero: true);
                    player.Play();
                    shouldAcquirePower = true;
                }
                else
                {
                    dsp.BeginTransportFadeIn(durationMs: 15.0, startFromZero: true);
                    shouldReleasePower = true;
                }
            }
            catch (Exception ex)
            {
                failedHandles = new DetachedPlaybackHandles(player, sourceDisposable, device, dsp);
                CurrentFilePath = null;
                _gapless = null;
                _sourceWaveStream = null;
                _sourceSamples = null;
                _sourceDisposable = null;
                _dsp = null;
                _device = null;
                _cachedDeviceName = null;
                _cachedDeviceId = null;
                _cachedApoStatus = new(false, null);
                _output = null;
                _isAsioActive = false;
                _pausePending = false;
                _pauseRequested = false;
                shouldAcquirePower = false;
                shouldReleasePower = true;
                signalSnapshotToNotify = ResetSignalPathToStandbyLocked();
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (failedHandles.HasAny)
        {
            DisposeDetachedHandles(failedHandles);
        }
        if (shouldAcquirePower)
        {
            _powerGuard.Acquire();
        }
        else if (shouldReleasePower)
        {
            _powerGuard.ReleaseImmediate();
        }
        if (signalSnapshotToNotify is not null)
        {
            SignalPathChanged?.Invoke(signalSnapshotToNotify);
        }
        capturedException?.Throw();
    }

    public bool PreloadNextFile(string? nextFilePath, double nextReplayGainDb) =>
        PreloadNextFile(nextFilePath, nextReplayGainDb, null);

    public bool PreloadNextFile(string? nextFilePath, double nextReplayGainDb, GaplessDspTransition? dspTransition)
    {
        GaplessSampleProvider? activeGapless;
        bool currentIsDsd;
        lock (_engineLock)
        {
            if (_gapless is null || _output is null) return false;
            currentIsDsd = _isDsdSource;
            activeGapless = _gapless;
        }

        if (string.IsNullOrWhiteSpace(nextFilePath))
        {
            activeGapless.ClearStagedNext();
            return false;
        }

        var effectiveTransition = dspTransition ?? new GaplessDspTransition(nextReplayGainDb);
        if (string.Equals(activeGapless.StagedNextPath, nextFilePath, StringComparison.OrdinalIgnoreCase))
        {
            activeGapless.TryUpdateStagedTransition(nextFilePath, effectiveTransition);
            return true;
        }

        string ext = Path.GetExtension(nextFilePath).ToLowerInvariant();
        bool nextIsDsd = ext is ".dsf" or ".dff";
        if (currentIsDsd != nextIsDsd)
        {
            activeGapless.ClearStagedNext();
            return false;
        }

        try
        {
            WaveStream? ws = null;
            ISampleProvider sp;
            IDisposable disp;
            if (nextIsDsd)
            {
                var dsd = new DsdStreamReader(ToExtendedPathIfLong(nextFilePath));
                sp = dsd;
                disp = dsd;
            }
            else
            {
                (ws, sp, disp) = OpenPcmSourceWithLongPathFallback(nextFilePath);
            }

            if (sp.WaveFormat.SampleRate != activeGapless.WaveFormat.SampleRate
                || sp.WaveFormat.Channels != activeGapless.WaveFormat.Channels)
            {
                disp.Dispose();
                return false;
            }

            var nextReadAhead = new DecodeReadAheadSampleProvider(ws, sp, disp);
            lock (_engineLock)
            {
                if (!ReferenceEquals(_gapless, activeGapless))
                {
                    nextReadAhead.Dispose();
                    return false;
                }
                return activeGapless.TryStageNext(nextFilePath, ws, nextReadAhead, nextReadAhead, effectiveTransition);
            }
        }
        catch
        {
            return false;
        }
    }

    public void ClearPreloadedNext()
    {
        lock (_engineLock)
        {
            _gapless?.ClearStagedNext();
        }
    }

    public IDisposable SuspendIfPlayingFile(IEnumerable<string> targetFullPaths)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in targetFullPaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                try { targets.Add(Path.GetFullPath(p)); } catch { targets.Add(p); }
            }
        }
        if (targets.Count == 0) return NoopDisposable.Instance;

        DetachedPlaybackHandles detached;
        string savedPath;
        TimeSpan savedPos = TimeSpan.Zero;
        bool wasPlaying;
        lock (_engineLock)
        {
            _gapless?.ClearStagedNextIfMatching(targets);
            if (string.IsNullOrWhiteSpace(CurrentFilePath))
                return NoopDisposable.Instance;

            string activeFull;
            try { activeFull = Path.GetFullPath(CurrentFilePath); } catch { activeFull = CurrentFilePath; }
            if (!targets.Contains(activeFull))
                return NoopDisposable.Instance;

            try
            {
                savedPos = _gapless is not null
                    ? _gapless.Position
                    : _sourceSamples switch
                    {
                        AudioFileReader afr => afr.CurrentTime,
                        DsdStreamReader dsd => dsd.CurrentTime,
                        _ => TimeSpan.Zero,
                    };
            }
            catch { }

            wasPlaying = IsPlaying;
            savedPath = CurrentFilePath;
            detached = DetachPlaybackHandlesLocked();
        }
        DisposeDetachedHandles(detached);
        return new ResumePlaybackToken(this, savedPath, savedPos, wasPlaying);
    }

    private void ResumeSuspendedPlayback(string filePath, TimeSpan position, bool shouldPlay)
    {
        try
        {
            string probePath = ToExtendedPathIfLong(filePath);
            if (!File.Exists(filePath) && !File.Exists(probePath)) return;
            PlayFile(filePath, startPaused: !shouldPlay, initialPosition: position);
        }
        catch
        {
            // Do not crash on resume token Dispose if audio file became temporarily unavailable
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class ResumePlaybackToken(TonewiseAudioEngine engine, string path, TimeSpan pos, bool play) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                engine.ResumeSuspendedPlayback(path, pos, play);
            }
        }
    }

    private DesktopSignalPathState? ApplyRuntimeDspConfigLocked(bool reloadFir = false)
    {
        UpdateBypassSnapshotLocked();
        if (_dsp is null) return null;
        ConfigureDspLocked(_dsp, reloadFir);
        _dsp.IsBypassed = ShouldBypassDspLocked(CurrentSignalPath.IsExclusive);
        return UpdateSignalPathSnapshotLocked(CurrentSignalPath.IsExclusive);
    }

    private static bool HasNonIdentityFilter(BiquadSpec f) =>
        Math.Abs(f.GainDb) > 1e-4 || f.Kind is BiquadKind.LowPass or BiquadKind.HighPass;

    private void UpdateBypassSnapshotLocked()
    {
        _bypassSnapshot = new BypassSnapshot(
            HasEq: _activeProfile is not null && _activeProfile.Filters.Any(HasNonIdentityFilter),
            ProfilePreampDb: _activeProfile?.PreampDb ?? 0.0,
            HasIso: _iso226Enabled && _volumeAttenuationDb < -0.5,
            HasVolume: _isMuted || _volumeAttenuationDb < -0.05,
            HasFir: _hasFirWavFile || (_firLeftIr is not null && _firRightIr is not null),
            HasExtended: _extendedDsp.Enabled && _extendedDsp.HasNonIdentityProcessing());
    }

    private void ConfigureDspLocked(DspSampleProvider dsp, bool reloadFir)
    {
        UpdateBypassSnapshotLocked();
        dsp.Configure(_activeProfile, _activeReplayGainDb);
        dsp.ConfigureIso226(_iso226Enabled, _iso226RefSplDb, _volumeAttenuationDb);
        dsp.ConfigureMasterVolume(_volumeAttenuationDb, _isMuted);
        dsp.SetSleepFadeLinear(_sleepFadeLinear);
        dsp.ConfigureDither(_tpdfDitherEnabled, 24);
        bool extActive = _extendedDsp.Enabled;
        bool firLoaded = _hasFirWavFile || (_firLeftIr is not null && _firRightIr is not null);
        bool hasEq = _activeProfile is not null && _activeProfile.Filters.Any(HasNonIdentityFilter);
        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: extActive && _extendedDsp.GainMatchedBypass,
            rmsShiftDbOverride: null,
            crossfeedEnabled: extActive && _extendedDsp.CrossfeedEnabled,
            crossfeedCutoffHz: _extendedDsp.CrossfeedCutoffHz,
            crossfeedFeedDb: _extendedDsp.CrossfeedFeedDb,
            crossfeedDelayMs: _extendedDsp.CrossfeedDelayMs,
            balanceDb: extActive ? _extendedDsp.BalanceDb : 0.0,
            monoFold: extActive && _extendedDsp.MonoFold,
            limiterEnabled: _extendedDsp.LimiterEnabled && (extActive || hasEq || firLoaded || Math.Abs(_activeReplayGainDb) > 1e-4),
            limiterCeilingDb: _extendedDsp.LimiterCeilingDb);
        if (reloadFir)
        {
            bool wavExists = !string.IsNullOrWhiteSpace(_firWavPath) && File.Exists(_firWavPath);
            _hasFirWavFile = wavExists;
            if (wavExists)
            {
                dsp.ConfigureFirFromWav(_firWavPath!, _firDryWetMix);
            }
            else if (_firLeftIr is not null && _firRightIr is not null)
            {
                dsp.ConfigureFirImpulse(_firLeftIr, _firRightIr, _firDryWetMix, _firLabel);
            }
            else
            {
                dsp.ConfigureFirImpulse(null, null);
            }
            UpdateBypassSnapshotLocked();
        }
        else
        {
            dsp.SetFirDryWetMix(_firDryWetMix);
        }
    }

    private static bool ShouldBypassDspFromSnapshot(BypassSnapshot snapshot, bool exclusiveOk, bool isDsd, double replayGainDb)
    {
        if (!exclusiveOk || isDsd) return false;
        bool hasPreampOrRg = Math.Abs(snapshot.ProfilePreampDb) >= 1e-6 || Math.Abs(replayGainDb) >= 1e-6;
        return !snapshot.HasEq && !hasPreampOrRg && !snapshot.HasIso && !snapshot.HasFir && !snapshot.HasVolume && !snapshot.HasExtended;
    }

    private bool ShouldBypassDspLocked(bool exclusiveOk) =>
        ShouldBypassDspFromSnapshot(_bypassSnapshot, exclusiveOk, _isDsdSource, _activeReplayGainDb);

    public void TogglePlayPause()
    {
        bool acquirePower = false;
        bool releasePower = false;
        lock (_engineLock)
        {
            if (_output is null) return;
            if (!_pausePending && _output.PlaybackState == PlaybackState.Playing)
            {
                BeginSmoothPauseLocked();
                releasePower = true;
            }
            else
            {
                ResumeFromPauseLocked();
                acquirePower = true;
            }
        }
        if (releasePower) _powerGuard.ReleaseImmediate();
        else if (acquirePower) _powerGuard.Acquire();
    }

    public void Pause()
    {
        bool releasePower = false;
        lock (_engineLock)
        {
            if (_output is not null && !_pausePending && _output.PlaybackState == PlaybackState.Playing)
            {
                BeginSmoothPauseLocked();
                releasePower = true;
            }
        }
        if (releasePower) _powerGuard.ReleaseImmediate();
    }

    private void BeginSmoothPauseLocked()
    {
        if (_output is null || _pausePending || _output.PlaybackState != PlaybackState.Playing)
        {
            return;
        }

        _pauseRequested = true;
        _pausePending = true;
        long gen = Interlocked.Increment(ref _transportOpGen);
        IWavePlayer capturedOutput = _output;
        DspSampleProvider? capturedDsp = _dsp;
        int sampleRate = Math.Max(capturedDsp?.WaveFormat.SampleRate ?? 48000, 8000);
        int latencyMs = Math.Clamp(_outputLatencyMs, 40, 180);
        int requiredSilenceFrames = (int)Math.Ceiling(sampleRate * (latencyMs / 1000.0));
        int maxWaitMs = latencyMs + 45;

        capturedDsp?.BeginTransportFadeOut(durationMs: 15.0);

        _ = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                if (Volatile.Read(ref _transportOpGen) != gen)
                {
                    return;
                }
                if (capturedDsp is null || capturedDsp.HasDrainedPauseSilence(requiredSilenceFrames))
                {
                    break;
                }
                Thread.Sleep(5);
            }

            lock (_engineLock)
            {
                if (Volatile.Read(ref _transportOpGen) == gen && ReferenceEquals(_output, capturedOutput))
                {
                    try
                    {
                        if (capturedOutput.PlaybackState == PlaybackState.Playing)
                        {
                            capturedOutput.Pause();
                        }
                    }
                    catch
                    {
                        // Ignore device pause transient during teardown
                    }
                    _pausePending = false;
                }
            }
        });
    }

    private void ResumeFromPauseLocked()
    {
        if (_output is null) return;
        Interlocked.Increment(ref _transportOpGen);
        _pauseRequested = false;

        if (_pausePending)
        {
            _pausePending = false;
            _dsp?.BeginTransportFadeIn(durationMs: 15.0, startFromZero: false);
            if (_output.PlaybackState != PlaybackState.Playing)
            {
                _output.Play();
            }
        }
        else if (_output.PlaybackState != PlaybackState.Playing)
        {
            _dsp?.BeginTransportFadeIn(durationMs: 15.0, startFromZero: true);
            _output.Play();
        }
    }

    public bool ConsumePendingNaturalEnd()
    {
        lock (_engineLock)
        {
            if ((_dsp?.ReachedEndOfStream == true || _gapless?.ReachedEndOfStream == true) && !IsPlaying)
            {
                return Interlocked.CompareExchange(ref _playbackEndedSignaled, 1, 0) == 0;
            }
            return false;
        }
    }

    public void Seek(double fraction)
    {
        lock (_engineLock)
        {
            bool resumeFadeIn = !_pausePending && _output?.PlaybackState == PlaybackState.Playing;
            if (resumeFadeIn)
            {
                _dsp?.BeginSeekSilenceHold();
            }
            _dsp?.ResetEndOfStream();
            Interlocked.Exchange(ref _playbackEndedSignaled, 0);
            double clamped = Math.Clamp(fraction, 0.0, 1.0);
            if (_gapless is not null)
            {
                _gapless.Seek(clamped);
            }
            else if (_sourceWaveStream is not null && _sourceWaveStream.TotalTime.TotalSeconds > 0)
            {
                _sourceWaveStream.CurrentTime = TimeSpan.FromSeconds(_sourceWaveStream.TotalTime.TotalSeconds * clamped);
            }
            else if (_sourceSamples is DsdStreamReader dsd)
            {
                dsd.SeekFraction(clamped);
            }
            if (resumeFadeIn)
            {
                _dsp?.ResetFilterStatesAndBeginFadeIn(durationMs: 12.0, startFromZero: true);
            }
            else
            {
                _dsp?.ResetFilterStates();
            }
        }
    }

    public void Stop()
    {
        DetachedPlaybackHandles detached;
        Task? pendingTeardown;
        DesktopSignalPathState standby;
        lock (_engineLock)
        {
            detached = DetachPlaybackHandlesLocked();
            pendingTeardown = _pendingErrorTeardown;
            _pendingErrorTeardown = null;
            standby = ResetSignalPathToStandbyLocked();
        }
        DisposeDetachedHandles(detached);
        if (pendingTeardown is not null)
        {
            try { pendingTeardown.Wait(500); } catch { }
        }
        _powerGuard.ReleaseImmediate();
        SignalPathChanged?.Invoke(standby);
    }

    private DesktopSignalPathState ResetSignalPathToStandbyLocked()
    {
        CurrentSignalPath = new DesktopSignalPathState(
            "System Default",
            "Idle — Standby",
            0,
            0,
            false,
            false,
            0,
            0.0,
            SignalPathBadge: "Idle — Standby");
        return CurrentSignalPath;
    }

    private DesktopSignalPathState UpdateSignalPathSnapshotLocked(bool isExclusive)
    {
        var source = _sourceSamples;
        var dsp = _dsp;
        WaveFormat? outFmt = null;
        try
        {
            outFmt = (_output as WasapiOut)?.OutputWaveFormat ?? _output?.OutputWaveFormat;
        }
        catch
        {
            outFmt = null;
        }

        string devName = _isAsioActive && !string.IsNullOrWhiteSpace(_asioDriverName)
            ? $"ASIO: {_asioDriverName}"
            : (_cachedDeviceName ?? "System Default");
        string shareMode = _isAsioActive
            ? "ASIO Direct Hardware"
            : (isExclusive ? "WASAPI Exclusive" : "WASAPI Shared");
        bool bypassed = dsp?.IsBypassed ?? false;
        bool identityPassthrough = dsp?.IsIdentityPassthrough ?? false;
        var apoStatus = _isAsioActive ? new ApoConflictStatus(false, null) : _cachedApoStatus;

        int sourceRate = source is DsdStreamReader dsdReader
            ? dsdReader.DsdSampleRateHz
            : (source?.WaveFormat.SampleRate ?? 0);
        bool sharedResampled = !_isDsdSource && !isExclusive && !_isAsioActive && outFmt is not null && source is not null && outFmt.SampleRate != source.WaveFormat.SampleRate;

        string badge;
        if (_isDsdSource)
        {
            if (_isAsioActive)
                badge = "DSD → 352.8kHz DXD PCM (ASIO)";
            else if (isExclusive)
                badge = "DSD → 352.8kHz DXD PCM (WASAPI Exclusive)";
            else
                badge = "DSD → 352.8kHz DXD PCM (WASAPI Shared · Resampled)";
        }
        else if (_isAsioActive)
        {
            badge = bypassed ? "ASIO · Bit-Perfect" : "ASIO · 64-Bit DSP";
        }
        else if (isExclusive && bypassed)
        {
            badge = "WASAPI Exclusive · Bit-Perfect";
        }
        else if (isExclusive)
        {
            badge = _tpdfDitherEnabled && !identityPassthrough
                ? "WASAPI Exclusive · 64-Bit DSP + 24-Bit TPDF"
                : "WASAPI Exclusive · 64-Bit DSP";
        }
        else if (apoStatus.HasConflict)
        {
            badge = sharedResampled
                ? "WASAPI Shared · 64-Bit DSP (APO Conflict Guard · Resampled)"
                : "WASAPI Shared · 64-Bit DSP (APO Conflict Guard)";
        }
        else
        {
            badge = sharedResampled
                ? "WASAPI Shared · 64-Bit DSP (Resampled)"
                : "WASAPI Shared · 64-Bit DSP";
        }

        int bitsPerSample = _isDsdSource ? 32 : (outFmt?.BitsPerSample ?? source?.WaveFormat.BitsPerSample ?? 0);
        int sampleRate = _isDsdSource ? 352_800 : (sharedResampled ? outFmt!.SampleRate : (outFmt?.SampleRate ?? sourceRate));

        CurrentSignalPath = new DesktopSignalPathState(
            OutputDeviceName: devName,
            ShareModeLabel: shareMode,
            SampleRateHz: sampleRate,
            BitsPerSample: bitsPerSample,
            IsExclusive: isExclusive,
            DspBypassed: bypassed,
            BiquadCount: dsp?.ActiveFilterCount ?? 0,
            EffectivePreampDb: dsp?.EffectivePreampDb ?? 0.0,
            Iso226Active: dsp?.IsIso226Active ?? false,
            FirTapCount: dsp?.FirTapCount ?? 0,
            TpdfDitherActive: !bypassed && !identityPassthrough && (dsp?.IsTpdfDitherEnabled ?? false),
            HasSystemApoConflict: apoStatus.HasConflict,
            SystemApoWarning: apoStatus.WarningMessage,
            SignalPathBadge: badge,
            CrossfeedActive: !bypassed && (dsp?.IsCrossfeedActive ?? false),
            GainMatchedBypass: !bypassed && (dsp?.IsGainMatchedBypass ?? false),
            LimiterActive: !bypassed && (dsp?.IsLimiterActive ?? false)
        );
        return CurrentSignalPath;
    }

    private readonly record struct DetachedPlaybackHandles(
        IWavePlayer? Output,
        IDisposable? SourceDisposable,
        MMDevice? Device,
        DspSampleProvider? Dsp = null)
    {
        public bool HasAny => Output is not null || SourceDisposable is not null || Device is not null || Dsp is not null;
    }

    private DetachedPlaybackHandles DetachPlaybackHandlesLocked()
    {
        Interlocked.Increment(ref _transportOpGen);
        _pausePending = false;
        _pauseRequested = false;
        CurrentFilePath = null;
        var detached = new DetachedPlaybackHandles(_output, _sourceDisposable, _device, _dsp);
        _output = null;
        _sourceDisposable = null;
        _gapless = null;
        _sourceWaveStream = null;
        _sourceSamples = null;
        _dsp = null;
        _device = null;
        _cachedDeviceName = null;
        _cachedDeviceId = null;
        _cachedApoStatus = new(false, null);
        _isAsioActive = false;
        return detached;
    }

    private static void DisposeDetachedHandles(DetachedPlaybackHandles detached)
    {
        try { detached.Output?.Stop(); } catch { }
        try { detached.Output?.Dispose(); } catch { }
        try { detached.Dsp?.Dispose(); } catch { }
        try { detached.SourceDisposable?.Dispose(); } catch { }
        try { detached.Device?.Dispose(); } catch { }
    }

    private static TimeSpan CapturePositionSafely(GaplessSampleProvider? gapless, WaveStream? waveStream, ISampleProvider? sourceSamples)
    {
        try
        {
            if (gapless is not null) return gapless.Position;
            if (waveStream is not null) return waveStream.CurrentTime;
            return sourceSamples switch
            {
                AudioFileReader afr => afr.CurrentTime,
                DsdStreamReader dsd => dsd.CurrentTime,
                _ => TimeSpan.Zero,
            };
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static TimeSpan CaptureDurationSafely(GaplessSampleProvider? gapless, WaveStream? waveStream, ISampleProvider? sourceSamples)
    {
        try
        {
            if (gapless is not null) return gapless.Duration;
            if (waveStream is not null) return waveStream.TotalTime;
            return sourceSamples switch
            {
                AudioFileReader afr => afr.TotalTime,
                DsdStreamReader dsd => dsd.TotalTime,
                _ => TimeSpan.Zero,
            };
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string ToExtendedPathIfLong(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return Path.IsPathRooted(path) && path.Length >= 240 ? @"\\?\" + path : path;
    }

    private static (WaveStream Stream, ISampleProvider Samples, IDisposable Disposable) OpenPcmSourceWithLongPathFallback(string filePath)
    {
        if (filePath.Length < 240)
        {
            try
            {
                var reader = new AudioFileReader(filePath);
                return (reader, reader, reader);
            }
            catch (Exception ex) when (ex is PathTooLongException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
            }
        }

        string extPath = pathPrefixEnsure(filePath);
        try
        {
            var reader = new AudioFileReader(extPath);
            return (reader, reader, reader);
        }
        catch
        {
            var fs = new FileStream(extPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                var mf = new StreamMediaFoundationReader(fs);
                return (mf, mf.ToSampleProvider(), new CompositeSourceDisposable(mf, fs));
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        static string pathPrefixEnsure(string raw) =>
            raw.StartsWith(@"\\?\", StringComparison.Ordinal) ? raw : (Path.IsPathRooted(raw) ? @"\\?\" + raw : raw);
    }

    private sealed class CompositeSourceDisposable(IDisposable primary, IDisposable secondary) : IDisposable
    {
        public void Dispose()
        {
            try { primary.Dispose(); } finally { secondary.Dispose(); }
        }
    }

    public void Dispose()
    {
        Stop();
        if (_ownsPowerGuard)
        {
            _powerGuard.Dispose();
        }
    }
}
