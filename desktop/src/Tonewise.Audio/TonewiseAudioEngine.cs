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
    double EffectivePreampDb
);

public sealed class TonewiseAudioEngine : IDisposable
{
    private readonly Lock _engineLock = new();
    private WasapiOut? _output;
    private AudioFileReader? _reader;
    private DspSampleProvider? _dsp;
    private MMDevice? _device;

    private EqualizerApoProfile? _activeProfile;
    private double _activeReplayGainDb;
    private bool _preferExclusive = true;
    private string? _selectedDeviceId;

    public event Action<DesktopSignalPathState>? SignalPathChanged;
    public event Action? PlaybackEnded;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public DesktopSignalPathState CurrentSignalPath { get; private set; } =
        new("System Default", "WASAPI Shared", 0, 0, false, false, 0, 0.0);

    public void SetPreferences(bool preferExclusive, string? deviceId, EqualizerApoProfile? profile, double replayGainDb)
    {
        _preferExclusive = preferExclusive;
        _selectedDeviceId = deviceId;
        _activeProfile = profile;
        _activeReplayGainDb = replayGainDb;

        lock (_engineLock)
        {
            if (_dsp is not null)
            {
                _dsp.Configure(profile, replayGainDb);
                _dsp.IsBypassed = preferExclusive && CurrentSignalPath.IsExclusive && profile is null && Math.Abs(replayGainDb) < 1e-6;
                UpdateSignalPathSnapshot(CurrentSignalPath.IsExclusive);
            }
        }
    }

    public void PlayFile(string filePath)
    {
        lock (_engineLock)
        {
            StopInternal();

            var reader = new AudioFileReader(filePath);
            var dsp = new DspSampleProvider(reader);
            dsp.Configure(_activeProfile, _activeReplayGainDb);

            var device = AudioDeviceRouter.ResolveDevice(_selectedDeviceId);
            bool exclusiveOk = false;
            WasapiOut? wasapi = null;

            if (device is not null && _preferExclusive)
            {
                try
                {
                    wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 60);
                    wasapi.Init(dsp);
                    exclusiveOk = true;
                }
                catch
                {
                    wasapi?.Dispose();
                    wasapi = null;
                }
            }

            if (wasapi is null)
            {
                wasapi = device is not null
                    ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100)
                    : new WasapiOut(AudioClientShareMode.Shared, 100);
                wasapi.Init(dsp);
            }

            // Bit-perfect bypass when Exclusive mode runs without active EQ/RG adjustments
            dsp.IsBypassed = exclusiveOk && _activeProfile is null && Math.Abs(_activeReplayGainDb) < 1e-6;

            wasapi.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is null && _reader is not null && _reader.Position >= _reader.Length - 1024)
                {
                    PlaybackEnded?.Invoke();
                }
            };

            _reader = reader;
            _dsp = dsp;
            _device = device;
            _output = wasapi;

            UpdateSignalPathSnapshot(exclusiveOk);
            wasapi.Play();
        }
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

    public void Seek(double fraction)
    {
        lock (_engineLock)
        {
            if (_reader is null) return;
            double clamped = Math.Clamp(fraction, 0.0, 1.0);
            _reader.CurrentTime = TimeSpan.FromSeconds(_reader.TotalTime.TotalSeconds * clamped);
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
        var reader = _reader;
        var dsp = _dsp;
        string devName = _device?.FriendlyName ?? "System Default";
        string shareMode = isExclusive ? "WASAPI Exclusive (Event-Driven)" : "WASAPI Shared";
        CurrentSignalPath = new DesktopSignalPathState(
            OutputDeviceName: devName,
            ShareModeLabel: shareMode,
            SampleRateHz: reader?.WaveFormat.SampleRate ?? 0,
            BitsPerSample: reader?.WaveFormat.BitsPerSample ?? 0,
            IsExclusive: isExclusive,
            DspBypassed: dsp?.IsBypassed ?? false,
            BiquadCount: dsp?.ActiveFilterCount ?? 0,
            EffectivePreampDb: dsp?.EffectivePreampDb ?? 0.0
        );
        SignalPathChanged?.Invoke(CurrentSignalPath);
    }

    private void StopInternal()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
        _dsp = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
