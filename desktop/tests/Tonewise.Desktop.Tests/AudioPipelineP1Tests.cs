using Tonewise.Audio;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class AudioPipelineP1Tests
{
    [Fact]
    public void EqualizerApo_ParsesDualChannelAndTonewiseMeta_AndFiltersAsymmetrically()
    {
        const string apo = """
            # Tonewise-Meta: Device=Genelec 8030C | FirPath=room_drc.wav | Iso226=true | RefSpl=82
            Preamp: -3.0 dB
            Channel: ALL
            Filter: ON PK Fc 1000 Hz Gain -2.0 dB Q 1.0
            Channel: L
            Filter: ON PK Fc 120 Hz Gain -6.0 dB Q 4.0
            Channel: R
            Filter: ON HSC Fc 6000 Hz Gain 3.0 dB Q 0.71
            """;

        var profile = EqualizerApoProfile.Parse(apo);
        Assert.Equal("Genelec 8030C", profile.BoundDeviceName);
        Assert.Equal("room_drc.wav", profile.FirImpulsePath);
        Assert.True(profile.EnableIso226);
        Assert.Equal(82.0, profile.ReferenceSplDb);
        Assert.Equal(3, profile.Filters.Count);
        Assert.Equal(BiquadChannelScope.All, profile.Filters[0].Channel);
        Assert.Equal(BiquadChannelScope.Left, profile.Filters[1].Channel);
        Assert.Equal(BiquadChannelScope.Right, profile.Filters[2].Channel);
        Assert.Equal(-3.0, profile.ComputeEffectivePreampDb(0.0), 5);

        // Verify independent L/R filtering on identical stereo sine sweep (120 Hz)
        const int frames = 4096;
        float[] stereoSamples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float s = 0.5f * MathF.Sin(2f * MathF.PI * 120f * i / 48000f);
            stereoSamples[i * 2] = s;
            stereoSamples[i * 2 + 1] = s;
        }

        var dsp = new DspSampleProvider(new ArraySampleProvider(stereoSamples, 48000, 2));
        dsp.Configure(profile, 0.0);
        dsp.ConfigureDither(false);
        dsp.IsBypassed = false;

        float[] outBuf = new float[stereoSamples.Length];
        int read = dsp.Read(outBuf, 0, outBuf.Length);
        Assert.Equal(stereoSamples.Length, read);

        // Measure tail RMS after filter settles: Left (120Hz -6dB notch) must be significantly quieter than Right
        double lSq = 0.0, rSq = 0.0;
        for (int i = 2048; i < frames; i++)
        {
            lSq += outBuf[i * 2] * outBuf[i * 2];
            rSq += outBuf[i * 2 + 1] * outBuf[i * 2 + 1];
        }
        double leftRms = Math.Sqrt(lSq / (frames - 2048));
        double rightRms = Math.Sqrt(rSq / (frames - 2048));
        Assert.True(leftRms < rightRms * 0.65, $"Expected L notch RMS ({leftRms:F4}) << R RMS ({rightRms:F4})");
    }

    [Fact]
    public void Iso226LoudnessNode_BoostsLowAndHighRelativeTo1kHz_WhenAttenuated()
    {
        var iso = new Iso226LoudnessNode(48000, 2);
        iso.Configure(isEnabled: true, referenceSplDb: 80.0, volumeAttenuationDb: -25.0);

        Assert.True(iso.ActiveLowBoostDb >= 6.0);
        Assert.True(iso.ActiveHighBoostDb >= 2.5);

        double rmsLow = MeasureSineGain(freqHz: 50.0, iso, channel: 0);
        double rmsMid = MeasureSineGain(freqHz: 1000.0, iso, channel: 1);
        Assert.True(rmsLow > rmsMid * 1.4, $"Expected 50 Hz gain ({rmsLow:F3}) > 1 kHz gain ({rmsMid:F3})");

        iso.Configure(isEnabled: false, referenceSplDb: 80.0, volumeAttenuationDb: -25.0);
        Assert.Equal(0.42, iso.ProcessSample(0.42, 0), 10);
    }

    [Fact]
    public void PartitionedFirConvolver_PreservesDiracIdentity_AndHandlesLongTailImpulseAcrossPartitions()
    {
        var convolver = new PartitionedFirConvolver(2);

        // 1. Short Dirac delta [1.0, 0, ...] -> bit-exact passthrough
        convolver.LoadImpulse([1.0, 0.0, 0.0], [1.0, 0.0, 0.0], normalizeEnergy: false);
        Assert.Equal(0.75, convolver.ProcessSample(0.75, 0), 10);
        Assert.Equal(-0.25, convolver.ProcessSample(-0.25, 0), 10);

        // 2. Tail partition delay (> PartitionSize = 256) at exact tap index 300
        double[] delayedIr = new double[512];
        delayedIr[300] = 0.8;
        convolver.LoadImpulse(delayedIr, delayedIr, normalizeEnergy: false);

        double[] response = new double[400];
        for (int n = 0; n < response.Length; n++)
        {
            double x = n == 10 ? 0.5 : 0.0;
            response[n] = convolver.ProcessSample(x, 0);
        }

        // Expected peak at n = 10 + 300 = 310 with value 0.5 * 0.8 = 0.4
        Assert.Equal(0.4, response[310], 7);
        Assert.Equal(0.0, response[309], 7);
        Assert.Equal(0.0, response[311], 7);

        // 3. DryWetMix = 0.5 blends 50% dry + 50% wet
        convolver.Reset();
        convolver.LoadImpulse([0.0, 1.0], [0.0, 1.0], normalizeEnergy: false);
        convolver.DryWetMix = 0.5;
        Assert.Equal(0.5, convolver.ProcessSample(1.0, 0), 10);
        Assert.Equal(0.5, convolver.ProcessSample(0.0, 0), 10);
    }

    [Fact]
    public void TpdfDitherProcessor_StaysWithinOneLsb_AndZeroMean()
    {
        var dither = new TpdfDitherProcessor(deterministicSeed: 42)
        {
            IsEnabled = true,
            TargetBitDepth = 16,
        };
        double lsb16 = 1.0 / (1 << 15);
        double sumErr = 0.0;
        const int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            double outVal = dither.ProcessSample(0.25);
            double diff = outVal - 0.25;
            Assert.InRange(diff, -lsb16 - 1e-12, lsb16 + 1e-12);
            sumErr += diff;
        }
        Assert.True(Math.Abs(sumErr / count) < lsb16 * 0.05);
    }

    [Fact]
    public void SystemApoConflictDetector_FlagsSharedOnlyWhenEndpointMatches()
    {
        string endpointId = "{0.0.0.00000000}.{12345678-ABCD-1234-ABCD-1234567890AB}";
        string[] fakeRegistry = ["{12345678-ABCD-1234-ABCD-1234567890AB}"];

        var sharedConflict = SystemApoConflictDetector.CheckEndpointConflict(
            endpointId, AudioOutputMode.WasapiShared, fakeRegistry);
        Assert.True(sharedConflict.HasConflict);
        Assert.NotNull(sharedConflict.WarningMessage);

        var exclusiveOk = SystemApoConflictDetector.CheckEndpointConflict(
            endpointId, AudioOutputMode.WasapiExclusive, fakeRegistry);
        Assert.False(exclusiveOk.HasConflict);
    }

    private static double MeasureSineGain(double freqHz, Iso226LoudnessNode iso, int channel)
    {
        const int total = 4096;
        double sumSq = 0.0;
        for (int i = 0; i < total; i++)
        {
            double s = 0.5 * Math.Sin(2.0 * Math.PI * freqHz * i / 48000.0);
            double y = iso.ProcessSample(s, channel);
            if (i >= 2048) sumSq += y * y;
        }
        return Math.Sqrt(sumSq / (total - 2048)) / 0.35355;
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate, int channels) : NAudio.Wave.ISampleProvider
    {
        private int _pos;
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _pos);
            Array.Copy(samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
