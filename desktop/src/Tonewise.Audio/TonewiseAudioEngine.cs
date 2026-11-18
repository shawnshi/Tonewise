using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Tonewise.Audio;

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
    string SignalPathBadge = "Idle — Standby"
);

public sealed class TonewiseAudioEngine : IDisposable
{
    private readonly Lock _engineLock = new();
    private IWavePlayer? _output;
    private WaveStream? _sourceWaveStream;
    private ISampleProvider? _sourceSamples;
    private IDisposable? _sourceDisposable;
    private DspSampleProvider? _dsp;
    private MMDevice? _device;
    private int _playbackEndedSignaled;

    private EqualizerApoProfile? _activeProfile;
    private double _activeReplayGainDb;
    private bool _preferExclusive = true;
    private string? _selectedDeviceId;
    private string? _asioDriverName;
    private bool _iso226Enabled;
    private double _iso226RefSplDb = 80.0;
    private double _volumeAttenuationDb;
    private string? _firWavPath;
    private double[]? _firLeftIr;
    private double[]? _firRightIr;
    private string? _firLabel;
    private double _firDryWetMix = 1.0;
    private bool _tpdfDitherEnabled = true;
    private bool _isDsdSource;
    private bool _isAsioActive;

    public event Action<DesktopSignalPathState>? SignalPathChanged;
    public event Action? PlaybackEnded;

    public string? CurrentFilePath { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position
    {
        get
        {
            lock (_engineLock)
            {
                try
                {
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
        get => _volumeAttenuationDb;
        set
        {
            _volumeAttenuationDb = Math.Clamp(value, -60.0, 0.0);
            ApplyRuntimeDspConfig();
        }
    }

    private bool _isMuted;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            ApplyRuntimeDspConfig();
        }
    }

    public DesktopSignalPathState CurrentSignalPath { get; private set; } =
        new("System Default", "WASAPI Shared", 0, 0, false, false, 0, 0.0);

    public void SetPreferences(bool preferExclusive, string? deviceId, EqualizerApoProfile? profile, double replayGainDb)
    {
        _preferExclusive = preferExclusive;
        _selectedDeviceId = deviceId;
        _activeProfile = profile;
        _activeReplayGainDb = replayGainDb;

        if (profile is not null)
        {
            if (profile.EnableIso226)
            {
                _iso226Enabled = true;
                _iso226RefSplDb = profile.ReferenceSplDb;
            }
            if (!string.IsNullOrWhiteSpace(profile.FirImpulsePath) && File.Exists(profile.FirImpulsePath))
            {
                _firWavPath = profile.FirImpulsePath;
                _firLeftIr = null;
                _firRightIr = null;
            }
        }

        ApplyRuntimeDspConfig();
    }

    public void SetAcousticPipeline(
        bool iso226Enabled,
        double referenceSplDb,
        double volumeAttenuationDb,
        bool tpdfDitherEnabled,
        double firDryWetMix = 1.0)
    {
        _iso226Enabled = iso226Enabled;
        _iso226RefSplDb = Math.Clamp(referenceSplDb, 70.0, 90.0);
        _volumeAttenuationDb = Math.Clamp(volumeAttenuationDb, -60.0, 0.0);
        _tpdfDitherEnabled = tpdfDitherEnabled;
        _firDryWetMix = Math.Clamp(firDryWetMix, 0.0, 1.0);
        ApplyRuntimeDspConfig();
    }

    public void SetFirImpulseFile(string? wavPath, double dryWetMix = 1.0)
    {
        _firWavPath = wavPath;
        _firLeftIr = null;
        _firRightIr = null;
        _firDryWetMix = Math.Clamp(dryWetMix, 0.0, 1.0);
        ApplyRuntimeDspConfig(reloadFir: true);
    }

    public void SetFirImpulseMemory(double[]? leftIr, double[]? rightIr, double dryWetMix = 1.0, string? label = null)
    {
        _firWavPath = null;
        _firLeftIr = leftIr;
        _firRightIr = rightIr;
        _firLabel = label;
        _firDryWetMix = Math.Clamp(dryWetMix, 0.0, 1.0);
        ApplyRuntimeDspConfig(reloadFir: true);
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

    public void PlayFile(string filePath)
    {
        lock (_engineLock)
        {
            StopInternal();
            Interlocked.Exchange(ref _playbackEndedSignaled, 0);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            _isDsdSource = ext is ".dsf" or ".dff";

            WaveStream? sourceWaveStream = null;
            ISampleProvider? sourceSamples = null;
            IDisposable? sourceDisposable = null;
            IWavePlayer? player = null;
            MMDevice? device = null;
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

                var dsp = new DspSampleProvider(sourceSamples);
                ConfigureDspLocked(dsp, reloadFir: true);

                _asioDriverName = null;

                if (!string.IsNullOrWhiteSpace(_selectedDeviceId)
                    && _selectedDeviceId.StartsWith(AudioDeviceRouter.AsioIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string driver = _selectedDeviceId[AudioDeviceRouter.AsioIdPrefix.Length..];
                    try
                    {
                        var asio = new AsioOut(driver);
                        asio.Init(dsp.ToWaveProvider());
                        player = asio;
                        asioOk = true;
                        exclusiveOk = true;
                        _asioDriverName = driver;
                    }
                    catch
                    {
                        player?.Dispose();
                        player = null;
                    }
                }

                if (player is null)
                {
                    device = AudioDeviceRouter.ResolveDevice(_selectedDeviceId);
                    if (device is not null && _preferExclusive)
                    {
                        try
                        {
                            var wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 60);
                            wasapi.Init(dsp);
                            player = wasapi;
                            exclusiveOk = true;
                        }
                        catch
                        {
                            player?.Dispose();
                            player = null;
                        }
                    }

                    if (player is null)
                    {
                        var shared = device is not null
                            ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100)
                            : new WasapiOut(AudioClientShareMode.Shared, 100);
                        shared.Init(dsp);
                        player = shared;
                    }
                }

                _isAsioActive = asioOk;
                dsp.IsBypassed = ShouldBypassDspLocked(exclusiveOk);

                var capturedSource = sourceSamples;
                var capturedWaveStream = sourceWaveStream;
                var capturedDsp = dsp;
                var capturedPlayer = player;
                player.PlaybackStopped += (_, args) =>
                {
                    bool isActiveOutput;
                    bool finished = false;
                    lock (_engineLock)
                    {
                        isActiveOutput = ReferenceEquals(_output, capturedPlayer);
                        if (isActiveOutput)
                        {
                            try
                            {
                                finished = capturedDsp.ReachedEndOfStream || (capturedWaveStream is { TotalTime.TotalSeconds: > 0 } ws
                                    ? ws.CurrentTime >= ws.TotalTime - TimeSpan.FromMilliseconds(500)
                                    : capturedSource is DsdStreamReader dsd
                                      && dsd.TotalTime.TotalSeconds > 0
                                      && dsd.CurrentTime >= dsd.TotalTime - TimeSpan.FromMilliseconds(500));
                            }
                            catch
                            {
                                finished = capturedDsp.ReachedEndOfStream;
                            }
                        }
                    }
                    if (isActiveOutput && args.Exception is null && finished && Interlocked.CompareExchange(ref _playbackEndedSignaled, 1, 0) == 0)
                    {
                        PlaybackEnded?.Invoke();
                    }
                };

                CurrentFilePath = filePath;
                _sourceWaveStream = sourceWaveStream;
                _sourceSamples = sourceSamples;
                _sourceDisposable = sourceDisposable;
                _dsp = dsp;
                _device = device;
                _output = player;

                UpdateSignalPathSnapshot(exclusiveOk);
                player.Play();
            }
            catch
            {
                player?.Dispose();
                sourceDisposable?.Dispose();
                device?.Dispose();
                CurrentFilePath = null;
                _isAsioActive = false;
                throw;
            }
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

        lock (_engineLock)
        {
            if (string.IsNullOrWhiteSpace(CurrentFilePath))
                return NoopDisposable.Instance;

            string activeFull;
            try { activeFull = Path.GetFullPath(CurrentFilePath); } catch { activeFull = CurrentFilePath; }
            if (!targets.Contains(activeFull))
                return NoopDisposable.Instance;

            TimeSpan savedPos = TimeSpan.Zero;
            try
            {
                savedPos = _sourceSamples switch
                {
                    AudioFileReader afr => afr.CurrentTime,
                    DsdStreamReader dsd => dsd.CurrentTime,
                    _ => TimeSpan.Zero,
                };
            }
            catch { }

            bool wasPlaying = IsPlaying;
            string savedPath = CurrentFilePath;
            StopInternal();
            return new ResumePlaybackToken(this, savedPath, savedPos, wasPlaying);
        }
    }

    private void ResumeSuspendedPlayback(string filePath, TimeSpan position, bool shouldPlay)
    {
        try
        {
            string probePath = ToExtendedPathIfLong(filePath);
            if (!File.Exists(filePath) && !File.Exists(probePath)) return;
            PlayFile(filePath);
            lock (_engineLock)
            {
                if (_sourceWaveStream is not null && _sourceWaveStream.TotalTime.TotalSeconds > 0)
                {
                    _sourceWaveStream.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0.0, _sourceWaveStream.TotalTime.TotalSeconds));
                }
                else if (_sourceSamples is DsdStreamReader dsd && dsd.TotalTime.TotalSeconds > 0)
                {
                    dsd.SeekFraction(Math.Clamp(position.TotalSeconds / dsd.TotalTime.TotalSeconds, 0.0, 0.999));
                }
                if (!shouldPlay)
                {
                    _output?.Pause();
                }
            }
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

    private void ApplyRuntimeDspConfig(bool reloadFir = false)
    {
        lock (_engineLock)
        {
            if (_dsp is not null)
            {
                ConfigureDspLocked(_dsp, reloadFir);
                _dsp.IsBypassed = ShouldBypassDspLocked(CurrentSignalPath.IsExclusive);
                UpdateSignalPathSnapshot(CurrentSignalPath.IsExclusive);
            }
        }
    }

    private void ConfigureDspLocked(DspSampleProvider dsp, bool reloadFir)
    {
        dsp.Configure(_activeProfile, _activeReplayGainDb);
        dsp.ConfigureIso226(_iso226Enabled, _iso226RefSplDb, _volumeAttenuationDb);
        dsp.ConfigureMasterVolume(_volumeAttenuationDb, _isMuted);
        dsp.ConfigureDither(_tpdfDitherEnabled, 24);
        if (reloadFir)
        {
            if (!string.IsNullOrWhiteSpace(_firWavPath) && File.Exists(_firWavPath))
            {
                dsp.ConfigureFirFromWav(_firWavPath, _firDryWetMix);
            }
            else if (_firLeftIr is not null && _firRightIr is not null)
            {
                dsp.ConfigureFirImpulse(_firLeftIr, _firRightIr, _firDryWetMix, _firLabel);
            }
            else
            {
                dsp.ConfigureFirImpulse(null, null);
            }
        }
    }

    private bool ShouldBypassDspLocked(bool exclusiveOk)
    {
        if (!exclusiveOk) return false;
        bool hasEq = _activeProfile is not null && _activeProfile.Filters.Count > 0;
        bool hasPreampOrRg = _activeProfile is not null && Math.Abs(_activeProfile.PreampDb) >= 1e-6
                             || Math.Abs(_activeReplayGainDb) >= 1e-6;
        bool hasIso = _iso226Enabled && _volumeAttenuationDb < -0.5;
        bool hasVolume = _isMuted || _volumeAttenuationDb < -0.05;
        bool hasFir = (!string.IsNullOrWhiteSpace(_firWavPath) && File.Exists(_firWavPath))
                      || (_firLeftIr is not null && _firRightIr is not null);
        return !hasEq && !hasPreampOrRg && !hasIso && !hasFir && !hasVolume;
    }

    public void TogglePlayPause()
    {
        lock (_engineLock)
        {
            if (_output is null) return;
            if (_output.PlaybackState == PlaybackState.Playing)
            {
                _output.Pause();
            }
            else
            {
                _output.Play();
            }
        }
    }

    public bool ConsumePendingNaturalEnd()
    {
        lock (_engineLock)
        {
            if (_dsp?.ReachedEndOfStream == true && !IsPlaying)
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
            _dsp?.ResetEndOfStream();
            Interlocked.Exchange(ref _playbackEndedSignaled, 0);
            double clamped = Math.Clamp(fraction, 0.0, 1.0);
            if (_sourceWaveStream is not null && _sourceWaveStream.TotalTime.TotalSeconds > 0)
            {
                _sourceWaveStream.CurrentTime = TimeSpan.FromSeconds(_sourceWaveStream.TotalTime.TotalSeconds * clamped);
            }
            else if (_sourceSamples is DsdStreamReader dsd)
            {
                dsd.SeekFraction(clamped);
            }
        }
    }

    public void Stop()
    {
        lock (_engineLock)
        {
            StopInternal();
        }
    }

    private void UpdateSignalPathSnapshot(bool isExclusive)
    {
        var source = _sourceSamples;
        var dsp = _dsp;
        string devName = _isAsioActive && !string.IsNullOrWhiteSpace(_asioDriverName)
            ? $"ASIO: {_asioDriverName}"
            : (_device?.FriendlyName ?? "System Default");
        string shareMode = _isAsioActive
            ? "ASIO Direct Hardware"
            : (isExclusive ? "WASAPI Exclusive (Event-Driven)" : "WASAPI Shared");
        bool bypassed = dsp?.IsBypassed ?? false;
        var apoStatus = _isAsioActive
            ? new ApoConflictStatus(false, null)
            : SystemApoConflictDetector.CheckEndpointConflict(
                _device?.ID ?? _selectedDeviceId,
                isExclusive ? AudioOutputMode.WasapiExclusive : AudioOutputMode.WasapiShared
            );

        string badge;
        if (_isDsdSource && _isAsioActive)
        {
            badge = "DSD → 352.8kHz/64-Bit DXD (ASIO)";
        }
        else if (_isDsdSource && !bypassed)
        {
            badge = "DSD → 352.8kHz/64-Bit DXD (DSP Active)";
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
            badge = _tpdfDitherEnabled
                ? "WASAPI Exclusive · 64-Bit DSP + 24-Bit TPDF"
                : "WASAPI Exclusive · 64-Bit DSP";
        }
        else
        {
            badge = apoStatus.HasConflict
                ? "WASAPI Shared · 64-Bit DSP (APO Conflict Guard)"
                : "WASAPI Shared · 64-Bit DSP";
        }

        int bitsPerSample = _isDsdSource ? 1 : (source?.WaveFormat.BitsPerSample ?? 0);
        int sampleRate = source is DsdStreamReader dsdReader
            ? dsdReader.DsdSampleRateHz
            : (source?.WaveFormat.SampleRate ?? 0);

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
            TpdfDitherActive: !bypassed && (dsp?.IsTpdfDitherEnabled ?? false),
            HasSystemApoConflict: apoStatus.HasConflict,
            SystemApoWarning: apoStatus.WarningMessage,
            SignalPathBadge: badge
        );
        SignalPathChanged?.Invoke(CurrentSignalPath);
    }

    private void StopInternal()
    {
        CurrentFilePath = null;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _sourceDisposable?.Dispose();
        _sourceDisposable = null;
        _sourceWaveStream = null;
        _sourceSamples = null;
        _dsp = null;
        _device?.Dispose();
        _device = null;
        _isAsioActive = false;
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
    }
}
