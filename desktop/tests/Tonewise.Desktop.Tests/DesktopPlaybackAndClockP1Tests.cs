using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;
using Tonewise.Audio;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DesktopPlaybackAndClockP1Tests
{
    private sealed class SequenceSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _pos;

        public SequenceSampleProvider(float[] samples, int sampleRate = 48000, int channels = 2)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int remaining = _samples.Length - _pos;
            if (remaining <= 0) return 0;
            int toCopy = Math.Min(count, remaining);
            Array.Copy(_samples, _pos, buffer, offset, toCopy);
            _pos += toCopy;
            return toCopy;
        }
    }

    [Fact]
    public void BiquadFilter64_HotCoefficientUpdate_PreservesDelayState()
    {
        var warmUpdated = new BiquadFilter64(
            new BiquadSpec(BiquadKind.Peaking, 1000.0, 3.0, 1.41, BiquadChannelScope.All),
            sampleRateHz: 48000,
            channels: 2);
        var warmBaseline = new BiquadFilter64(
            new BiquadSpec(BiquadKind.Peaking, 1000.0, 3.0, 1.41, BiquadChannelScope.All),
            sampleRateHz: 48000,
            channels: 2);

        const int warmupSamples = 256;
        for (int i = 0; i < warmupSamples; i++)
        {
            double x = 0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * i / 48000.0);
            _ = warmUpdated.ProcessSample(x, 0);
            _ = warmBaseline.ProcessSample(x, 0);
        }

        warmUpdated.UpdateCoefficients(
            new BiquadSpec(BiquadKind.Peaking, 1000.0, 3.2, 1.41, BiquadChannelScope.All),
            sampleRateHz: 48000);
        var cold = new BiquadFilter64(
            new BiquadSpec(BiquadKind.Peaking, 1000.0, 3.2, 1.41, BiquadChannelScope.All),
            sampleRateHz: 48000,
            channels: 2);

        double nextInput = 0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * warmupSamples / 48000.0);
        double yWarmUpdated = warmUpdated.ProcessSample(nextInput, 0);
        double yWarmBaseline = warmBaseline.ProcessSample(nextInput, 0);
        double yCold = cold.ProcessSample(nextInput, 0);

        double warmDelta = Math.Abs(yWarmUpdated - yWarmBaseline);
        double coldDelta = Math.Abs(yCold - yWarmBaseline);
        Assert.True(warmDelta < 0.03, $"Expected small continuity step on hot update, got {warmDelta}");
        Assert.True(warmDelta < coldDelta * 0.25, $"Expected warmDelta ({warmDelta}) << coldDelta ({coldDelta})");
    }

    [Fact]
    public void LookaheadLimiter_TransientPeak_AttenuatesSmoothlyWithoutOvershoot()
    {
        const int frames = 480 + 32 + 480;
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float amp = (f >= 480 && f < 512) ? 2.5f : 0.1f;
            data[f * 2] = amp;
            data[f * 2 + 1] = amp;
        }

        var dsp = new DspSampleProvider(new SequenceSampleProvider(data, 48000, 2));
        dsp.Configure(profile: null, replayGainDb: 6.0);
        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: false,
            rmsShiftDbOverride: null,
            crossfeedEnabled: false,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: true,
            limiterCeilingDb: -0.2);

        double ceilingLinear = Math.Pow(10.0, -0.2 / 20.0);
        var buf = new float[data.Length];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = dsp.Read(buf, totalRead, buf.Length - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }

        Assert.Equal(data.Length, totalRead);
        for (int i = 0; i < totalRead; i++)
        {
            Assert.True(float.IsFinite(buf[i]), $"Sample {i} is not finite: {buf[i]}");
            Assert.True(
                Math.Abs(buf[i]) <= ceilingLinear + 1e-3,
                $"Sample {i} exceeded limiter ceiling: {Math.Abs(buf[i])} > {ceilingLinear}");
        }
    }

    [Fact]
    public void PartitionedFirConvolver_ResamplesMismatchedRateAndLinksStereoEnergy()
    {
        string tempWav = Path.Combine(Path.GetTempPath(), $"tw_p1_fir_{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(tempWav, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = new float[480 * 2];
                samples[0] = 1.0f;
                samples[1] = 1.0f;
                writer.WriteSamples(samples, 0, samples.Length);
            }

            var (leftResampled, rightResampled) = PartitionedFirConvolver.ReadImpulseArraysFromWav(
                tempWav,
                targetSampleRateHz: 96000);
            Assert.Equal(960, leftResampled.Length);
            Assert.Equal(960, rightResampled.Length);

            var convolver = new PartitionedFirConvolver(2);
            convolver.LoadImpulse([2.0, 0.0], [0.5, 0.0], normalizeEnergy: true);
            Assert.Equal(1.0, convolver.ProcessSample(1.0, 0), 5);
            Assert.Equal(0.25, convolver.ProcessSample(1.0, 1), 5);
        }
        finally
        {
            if (File.Exists(tempWav)) File.Delete(tempWav);
        }
    }

    [Fact]
    public void GaplessSampleProvider_ReturnsTrackATailBeforeTransitioningToTrackB()
    {
        var trackAData = Enumerable.Repeat(0.5f, 10).ToArray();
        var trackBData = Enumerable.Repeat(0.5f, 10).ToArray();
        var trackA = new SequenceSampleProvider(trackAData, 48000, 2);
        var trackB = new SequenceSampleProvider(trackBData, 48000, 2);

        int transitionCount = 0;
        string? transitionedPath = null;
        double transitionedRg = 0.0;
        DspSampleProvider? dsp = null;

        using var gapless = new GaplessSampleProvider(
            "trackA.flac",
            initialWaveStream: null,
            initialSamples: trackA,
            initialDisposable: null,
            onTransitionSync: (path, rg) =>
            {
                transitionCount++;
                transitionedPath = path;
                transitionedRg = rg;
                dsp?.SetReplayGainDb(rg);
            });
        dsp = new DspSampleProvider(gapless) { IsTpdfDitherEnabled = false };
        dsp.SetReplayGainDb(0.0);

        Assert.True(gapless.TryStageNext("trackB.flac", null, trackB, null, -6.020599913279624));

        var outBuf = new float[20];
        int totalRead = dsp.Read(outBuf, 0, outBuf.Length);
        Assert.Equal(20, totalRead);
        Assert.Equal(1, transitionCount);
        Assert.Equal("trackB.flac", transitionedPath);
        Assert.Equal(-6.020599913279624, transitionedRg, 4);
        for (int i = 0; i < 10; i++)
        {
            Assert.InRange(outBuf[i], 0.5f - 1e-4f, 0.5f + 1e-4f);
        }
        for (int i = 10; i < 20; i++)
        {
            Assert.InRange(outBuf[i], 0.25f - 1e-4f, 0.25f + 1e-4f);
        }
    }

    [Fact]
    public void DsdStreamReader_MalformedDffOverflowChunk_ThrowsInvalidDataWithoutHang()
    {
        string tempDff = Path.Combine(Path.GetTempPath(), $"tw_p1_bad_{Guid.NewGuid():N}.dff");
        try
        {
            using (var fs = File.Create(tempDff))
            {
                byte[] header = new byte[4 + 8 + 4 + 4 + 8];
                Encoding.ASCII.GetBytes("FRM8", header.AsSpan(0, 4));
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4, 8), 1024UL);
                Encoding.ASCII.GetBytes("DSD ", header.AsSpan(12, 4));
                Encoding.ASCII.GetBytes("FVER", header.AsSpan(16, 4));
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(20, 8), ulong.MaxValue);
                fs.Write(header, 0, header.Length);
            }

            Assert.Throws<InvalidDataException>(() => new DsdStreamReader(tempDff));
        }
        finally
        {
            if (File.Exists(tempDff)) File.Delete(tempDff);
        }
    }
}
