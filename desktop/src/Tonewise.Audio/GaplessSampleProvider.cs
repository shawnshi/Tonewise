using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// Seamless gapless PCM stream provider that transitions into a pre-opened next track
/// of matching sample rate & channel count inside the same audio buffer pull without
/// closing or re-initializing WASAPI/ASIO hardware output.
/// </summary>
public sealed class GaplessSampleProvider : ISampleProvider, IDisposable
{
    private readonly Lock _sourceLock = new();
    private readonly Action<string, double>? _onTransitionSync;

    private string _currentPath;
    private WaveStream? _currentWaveStream;
    private ISampleProvider _currentSamples;
    private volatile DecodeReadAheadSampleProvider? _currentReadAhead;
    private IDisposable? _currentDisposable;

    private string? _nextPath;
    private WaveStream? _nextWaveStream;
    private ISampleProvider? _nextSamples;
    private IDisposable? _nextDisposable;
    private double _nextReplayGainDb;

    private volatile bool _reachedEndOfStream;

    public GaplessSampleProvider(
        string initialPath,
        WaveStream? initialWaveStream,
        ISampleProvider initialSamples,
        IDisposable? initialDisposable,
        Action<string, double>? onTransitionSync = null)
    {
        _currentPath = initialPath;
        _currentWaveStream = initialWaveStream;
        _currentSamples = initialSamples;
        _currentReadAhead = initialSamples as DecodeReadAheadSampleProvider;
        _currentDisposable = initialDisposable;
        _onTransitionSync = onTransitionSync;
        WaveFormat = initialSamples.WaveFormat;
    }

    public event Action<string>? TrackTransitioned;

    public WaveFormat WaveFormat { get; }

    public string CurrentFilePath
    {
        get { lock (_sourceLock) return _currentPath; }
    }

    public string? StagedNextPath
    {
        get { lock (_sourceLock) return _nextPath; }
    }

    public bool ReachedEndOfStream => _reachedEndOfStream;

    public void ResetEndOfStream() => _reachedEndOfStream = false;

    public TimeSpan Position
    {
        get
        {
            var ra = _currentReadAhead;
            if (ra is not null) return ra.Position;
            lock (_sourceLock)
            {
                try
                {
                    if (_currentWaveStream is not null) return _currentWaveStream.CurrentTime;
                    if (_currentSamples is AudioFileReader afr) return afr.CurrentTime;
                    if (_currentSamples is DsdStreamReader dsd) return dsd.CurrentTime;
                }
                catch { }
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            var ra = _currentReadAhead;
            if (ra is not null) return ra.Duration;
            lock (_sourceLock)
            {
                try
                {
                    if (_currentWaveStream is not null) return _currentWaveStream.TotalTime;
                    if (_currentSamples is AudioFileReader afr) return afr.TotalTime;
                    if (_currentSamples is DsdStreamReader dsd) return dsd.TotalTime;
                }
                catch { }
                return TimeSpan.Zero;
            }
        }
    }

    public bool TryStageNext(
        string filePath,
        WaveStream? waveStream,
        ISampleProvider samples,
        IDisposable? disposable,
        double replayGainDb)
    {
        if (samples.WaveFormat.SampleRate != WaveFormat.SampleRate
            || samples.WaveFormat.Channels != WaveFormat.Channels)
        {
            disposable?.Dispose();
            return false;
        }

        lock (_sourceLock)
        {
            DisposeNextUnlocked();
            _nextPath = filePath;
            _nextWaveStream = waveStream;
            _nextSamples = samples;
            _nextDisposable = disposable;
            _nextReplayGainDb = replayGainDb;
            return true;
        }
    }

    public void ClearStagedNext()
    {
        lock (_sourceLock)
        {
            DisposeNextUnlocked();
        }
    }

    public void ClearStagedNextIfMatching(IReadOnlySet<string> targetFullPaths)
    {
        lock (_sourceLock)
        {
            if (string.IsNullOrWhiteSpace(_nextPath)) return;
            string full;
            try { full = Path.GetFullPath(_nextPath); } catch { full = _nextPath; }
            if (targetFullPaths.Contains(full))
            {
                DisposeNextUnlocked();
            }
        }
    }

    public void Seek(double fraction)
    {
        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        var ra = _currentReadAhead;
        if (ra is not null)
        {
            _reachedEndOfStream = false;
            ra.Seek(clamped);
            return;
        }
        lock (_sourceLock)
        {
            _reachedEndOfStream = false;
            if (_currentWaveStream is not null && _currentWaveStream.TotalTime.TotalSeconds > 0)
            {
                _currentWaveStream.CurrentTime = TimeSpan.FromSeconds(_currentWaveStream.TotalTime.TotalSeconds * clamped);
            }
            else if (_currentSamples is DsdStreamReader dsd)
            {
                dsd.SeekFraction(clamped);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int seg = ReadSegment(buffer, offset + totalRead, count - totalRead);
            if (seg <= 0) break;
            totalRead += seg;
        }
        return totalRead;
    }

    internal int ReadSegment(float[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n;
            string? transitionedPath = null;
            double transitionedRg = 0.0;
            IDisposable? oldDisposable = null;

            lock (_sourceLock)
            {
                n = _currentSamples.Read(buffer, offset + totalRead, count - totalRead);
                if (n > 0)
                {
                    totalRead += n;
                    continue;
                }

                // Never swap tracks mid-segment after Track A placed samples: return Track A's tail
                // so DspSampleProvider applies Track A's ReplayGain/bypass exclusively to Track A.
                if (totalRead > 0)
                {
                    break;
                }

                if (_nextSamples is null || string.IsNullOrWhiteSpace(_nextPath))
                {
                    _reachedEndOfStream = true;
                    break;
                }

                oldDisposable = _currentDisposable;
                _currentPath = _nextPath;
                _currentWaveStream = _nextWaveStream;
                _currentSamples = _nextSamples;
                _currentReadAhead = _nextSamples as DecodeReadAheadSampleProvider;
                _currentDisposable = _nextDisposable;
                transitionedPath = _currentPath;
                transitionedRg = _nextReplayGainDb;

                _nextPath = null;
                _nextWaveStream = null;
                _nextSamples = null;
                _nextDisposable = null;
                _nextReplayGainDb = 0.0;
                _reachedEndOfStream = false;
            }

            if (oldDisposable is not null)
            {
                ThreadPool.QueueUserWorkItem(static d =>
                {
                    try { ((IDisposable)d!).Dispose(); } catch { }
                }, oldDisposable);
            }

            if (transitionedPath is not null)
            {
                _onTransitionSync?.Invoke(transitionedPath, transitionedRg);
                string captured = transitionedPath;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { TrackTransitioned?.Invoke(captured); } catch { }
                });
            }
        }

        return totalRead;
    }

    private void DisposeNextUnlocked()
    {
        var d = _nextDisposable;
        _nextPath = null;
        _nextWaveStream = null;
        _nextSamples = null;
        _nextDisposable = null;
        _nextReplayGainDb = 0.0;
        try { d?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_sourceLock)
        {
            DisposeNextUnlocked();
            var cur = _currentDisposable;
            _currentDisposable = null;
            _currentWaveStream = null;
            _currentReadAhead = null;
            try { cur?.Dispose(); } catch { }
        }
    }
}
