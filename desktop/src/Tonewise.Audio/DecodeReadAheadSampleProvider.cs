using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// Lock-free consumer read-ahead ring buffer decoupling synchronous file decoding (FLAC/MP3/AAC/DSD)
/// from real-time WASAPI/ASIO audio pulls and UI timer position queries.
/// </summary>
public sealed class DecodeReadAheadSampleProvider : ISampleProvider, IDisposable
{
    private readonly Lock _decoderLock = new();
    private readonly object _ringLock = new();
    private readonly WaveStream? _waveStream;
    private readonly ISampleProvider _samples;
    private readonly IDisposable? _disposable;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _ringBuffer;
    private readonly int _capacity;
    private readonly float[] _decodeChunk;
    private readonly Thread _producerThread;

    private TimeSpan _duration;
    private int _readPos;
    private int _writePos;
    private int _bufferedCount;
    private int _partialFrameSamples;
    private long _consumedFrames;
    private bool _eof;
    private bool _seekInProgress;
    private long _seekGeneration;
    private volatile bool _disposed;

    public DecodeReadAheadSampleProvider(
        WaveStream? waveStream,
        ISampleProvider samples,
        IDisposable? disposable,
        int bufferDurationMs = 600)
    {
        _waveStream = waveStream;
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        _disposable = disposable;
        WaveFormat = samples.WaveFormat;

        _channels = Math.Max(WaveFormat.Channels, 1);
        _sampleRate = Math.Max(WaveFormat.SampleRate, 8000);
        int clampedMs = Math.Clamp(bufferDurationMs, 200, 2000);
        long desiredFloats = (long)_sampleRate * _channels * clampedMs / 1000L;
        int minFloats = 8192 * _channels;
        int maxFloats = (1_048_576 / _channels) * _channels;
        int rawCap = (int)Math.Clamp(desiredFloats, minFloats, Math.Max(minFloats, maxFloats));
        _capacity = Math.Max(_channels, (rawCap / _channels) * _channels);
        _ringBuffer = new float[_capacity];

        int chunkFloats = Math.Min(4096 * _channels, Math.Max(_channels, _capacity / 4));
        chunkFloats = Math.Max(_channels, (chunkFloats / _channels) * _channels);
        _decodeChunk = new float[chunkFloats];

        _duration = ReadTotalDurationSafely();

        _producerThread = new Thread(ProducerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Tonewise-AudioDecode",
        };
        _producerThread.Start();
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Position =>
        TimeSpan.FromSeconds(Interlocked.Read(ref _consumedFrames) / (double)_sampleRate);

    public TimeSpan Duration => _duration;

    public void Seek(double fraction)
    {
        if (_disposed) return;
        double clamped = Math.Clamp(fraction, 0.0, 1.0);

        lock (_ringLock)
        {
            if (_disposed) return;
            _seekInProgress = true;
            _seekGeneration++;
            Monitor.PulseAll(_ringLock);
        }

        try
        {
            lock (_decoderLock)
            {
                if (_disposed) return;

                long targetFrame = 0;
                if (_waveStream is not null && _waveStream.TotalTime.TotalSeconds > 0)
                {
                    TimeSpan targetTime = TimeSpan.FromSeconds(_waveStream.TotalTime.TotalSeconds * clamped);
                    _waveStream.CurrentTime = targetTime;
                    targetFrame = (long)Math.Round(targetTime.TotalSeconds * _sampleRate);
                    _duration = _waveStream.TotalTime;
                }
                else if (_samples is AudioFileReader afr && afr.TotalTime.TotalSeconds > 0)
                {
                    TimeSpan targetTime = TimeSpan.FromSeconds(afr.TotalTime.TotalSeconds * clamped);
                    afr.CurrentTime = targetTime;
                    targetFrame = (long)Math.Round(targetTime.TotalSeconds * _sampleRate);
                    _duration = afr.TotalTime;
                }
                else if (_samples is DsdStreamReader dsd)
                {
                    dsd.SeekFraction(clamped);
                    targetFrame = (long)Math.Round(dsd.CurrentTime.TotalSeconds * _sampleRate);
                    _duration = dsd.TotalTime;
                }

                lock (_ringLock)
                {
                    _readPos = 0;
                    _writePos = 0;
                    _bufferedCount = 0;
                    _partialFrameSamples = 0;
                    _eof = false;
                    Interlocked.Exchange(ref _consumedFrames, Math.Max(0L, targetFrame));
                }
            }
        }
        finally
        {
            lock (_ringLock)
            {
                _seekInProgress = false;
                Monitor.PulseAll(_ringLock);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        int totalRead = 0;

        while (totalRead < count)
        {
            bool waited = false;
            lock (_ringLock)
            {
                if (_disposed) break;

                if (_seekInProgress)
                {
                    int remaining = count - totalRead;
                    ClearFloatSamples(buffer, offset + totalRead, remaining);
                    totalRead += remaining;
                    break;
                }

                int waitAttempts = 0;
                while (_bufferedCount == 0 && !_eof && !_disposed && !_seekInProgress && waitAttempts < 5)
                {
                    Monitor.Wait(_ringLock, 15);
                    waitAttempts++;
                    waited = true;
                }

                if (_disposed) break;

                if (_seekInProgress)
                {
                    int remaining = count - totalRead;
                    ClearFloatSamples(buffer, offset + totalRead, remaining);
                    totalRead += remaining;
                    break;
                }

                if (_bufferedCount > 0)
                {
                    int toCopy = Math.Min(count - totalRead, _bufferedCount);
                    int first = Math.Min(toCopy, _capacity - _readPos);
                    CopyFloatSamples(_ringBuffer, _readPos, buffer, offset + totalRead, first);
                    int second = toCopy - first;
                    if (second > 0)
                    {
                        CopyFloatSamples(_ringBuffer, 0, buffer, offset + totalRead + first, second);
                    }
                    _readPos = (_readPos + toCopy) % _capacity;
                    _bufferedCount -= toCopy;
                    totalRead += toCopy;

                    _partialFrameSamples += toCopy;
                    long wholeFrames = _partialFrameSamples / _channels;
                    if (wholeFrames > 0)
                    {
                        _partialFrameSamples %= _channels;
                        Interlocked.Add(ref _consumedFrames, wholeFrames);
                    }

                    Monitor.PulseAll(_ringLock);
                    continue;
                }

                if (_eof)
                {
                    break;
                }

                // Ring temporarily empty while decoder thread is still running: emit bounded silence hold
                // rather than returning 0 so downstream WASAPI/Gapless never mistakes a stall for EOF.
                if (totalRead == 0 && waited)
                {
                    int silence = Math.Min(count, _channels * 256);
                    ClearFloatSamples(buffer, offset, silence);
                    totalRead = silence;
                }
                break;
            }
        }

        return totalRead;
    }

    private static void CopyFloatSamples(float[] src, int srcOffset, float[] dst, int dstOffset, int count)
    {
        // Avoid Array.Copy/AsSpan on dst: NAudio's WaveBuffer aliases a byte[] as float[]
        // inside SampleToWaveProvider, causing ArrayTypeMismatchException in Array.Copy.
        for (int i = 0; i < count; i++)
        {
            dst[dstOffset + i] = src[srcOffset + i];
        }
    }

    private static void ClearFloatSamples(float[] dst, int dstOffset, int count)
    {
        // Avoid Array.Clear on dst: on a WaveBuffer-aliased byte[], Array.Clear interprets
        // offset/count in bytes instead of 32-bit floats, leaving 75% of samples uncleared.
        for (int i = 0; i < count; i++)
        {
            dst[dstOffset + i] = 0f;
        }
    }

    private void ProducerLoop()
    {
        try
        {
            while (!_disposed)
            {
                int alignedFree;
                long capturedSeekGen;
                lock (_ringLock)
                {
                    while (!_disposed && (_seekInProgress || _eof || (_capacity - _bufferedCount) < _channels))
                    {
                        Monitor.Wait(_ringLock, 50);
                    }
                    if (_disposed) return;

                    int free = _capacity - _bufferedCount;
                    alignedFree = (free / _channels) * _channels;
                    if (alignedFree < _channels) continue;
                    capturedSeekGen = _seekGeneration;
                }

                int toDecode = Math.Min(_decodeChunk.Length, alignedFree);
                int decoded;
                lock (_decoderLock)
                {
                    if (_disposed) return;
                    if (_seekInProgress) continue;

                    try
                    {
                        decoded = _samples.Read(_decodeChunk, 0, toDecode);
                    }
                    catch
                    {
                        lock (_ringLock)
                        {
                            _eof = true;
                            Monitor.PulseAll(_ringLock);
                        }
                        return;
                    }

                    lock (_ringLock)
                    {
                        if (_disposed) return;
                        if (_seekInProgress || _seekGeneration != capturedSeekGen) continue;

                        if (decoded <= 0)
                        {
                            _eof = true;
                            Monitor.PulseAll(_ringLock);
                            continue;
                        }

                        int writable = Math.Min(decoded, _capacity - _bufferedCount);
                        int first = Math.Min(writable, _capacity - _writePos);
                        Array.Copy(_decodeChunk, 0, _ringBuffer, _writePos, first);
                        int second = writable - first;
                        if (second > 0)
                        {
                            Array.Copy(_decodeChunk, first, _ringBuffer, 0, second);
                        }
                        _writePos = (_writePos + writable) % _capacity;
                        _bufferedCount += writable;
                        Monitor.PulseAll(_ringLock);
                    }
                }
            }
        }
        catch
        {
            lock (_ringLock)
            {
                _eof = true;
                Monitor.PulseAll(_ringLock);
            }
        }
    }

    private TimeSpan ReadTotalDurationSafely()
    {
        try
        {
            if (_waveStream is not null) return _waveStream.TotalTime;
            if (_samples is AudioFileReader afr) return afr.TotalTime;
            if (_samples is DsdStreamReader dsd) return dsd.TotalTime;
        }
        catch
        {
            // Ignore stream duration query failure
        }
        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_ringLock)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_ringLock);
        }

        if (_producerThread.IsAlive && Thread.CurrentThread != _producerThread)
        {
            _producerThread.Join(1500);
        }

        lock (_decoderLock)
        {
            try { _disposable?.Dispose(); } catch { }
        }
    }
}
