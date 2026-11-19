using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DesktopPlaybackAndClockP0Tests
{
    private sealed class ZeroStereoSampleProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    [Fact]
    public void OledBurnInGuard_ComputeOrbitOffset_VisitsSixteenDistinctPointsBoundedWithinAmplitude()
    {
        var visited = new HashSet<(int, int)>();
        for (int tick = 0; tick < 16; tick++)
        {
            var (dx, dy) = OledBurnInGuard.ComputeOrbitOffset(tick, amplitudeDip: 12);
            Assert.InRange(dx, -12, 12);
            Assert.InRange(dy, -12, 12);
            visited.Add((dx, dy));
        }

        Assert.Equal(16, visited.Count);
        Assert.Equal(OledBurnInGuard.ComputeOrbitOffset(0, 12), OledBurnInGuard.ComputeOrbitOffset(16, 12));
        Assert.Equal(OledBurnInGuard.ComputeOrbitOffset(15, 12), OledBurnInGuard.ComputeOrbitOffset(-1, 12));
    }

    [Fact]
    public void DspSampleProvider_IsIdentityPassthrough_ReflectsFlatVsActiveProcessing()
    {
        var dsp = new DspSampleProvider(new ZeroStereoSampleProvider());
        Assert.True(dsp.IsIdentityPassthrough);

        dsp.Configure(
            EqualizerApoProfile.Parse("Filter: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.0"),
            replayGainDb: 0.0);
        Assert.False(dsp.IsIdentityPassthrough);

        dsp.Configure(profile: null, replayGainDb: 0.0);
        Assert.True(dsp.IsIdentityPassthrough);

        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: false,
            rmsShiftDbOverride: null,
            crossfeedEnabled: true,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: true,
            limiterCeilingDb: -0.2);
        Assert.False(dsp.IsIdentityPassthrough);

        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: false,
            rmsShiftDbOverride: null,
            crossfeedEnabled: false,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: false,
            limiterCeilingDb: -0.2);
        dsp.ConfigureMasterVolume(attenuationDb: -6.0, muted: false);
        Assert.False(dsp.IsIdentityPassthrough);
    }

    [Fact]
    public void TonewiseAudioEngine_StopResetsSignalPathToStandby_AndAutoEqExposesActiveProfile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p0_audio_" + Guid.NewGuid().ToString("N"));
        string profilesDir = Path.Combine(tempRoot, ".tonewise", "dsp_profiles");
        Directory.CreateDirectory(profilesDir);
        string eqPath = Path.Combine(profilesDir, "Sennheiser_HD600.txt");
        File.WriteAllText(eqPath, "# Tonewise-Meta: Device=HD 600\nPreamp: -3.5 dB\nFilter: ON PK Fc 3000 Hz Gain -2.0 dB Q 1.4\n");

        try
        {
            using var engine = new TonewiseAudioEngine();
            string? loaded = engine.TryAutoLoadDeviceDspProfile(tempRoot, "DAC Sennheiser HD 600");
            Assert.Equal(eqPath, loaded);
            Assert.NotNull(engine.ActiveProfile);
            Assert.Equal(-3.5, engine.ActiveProfile!.PreampDb, 3);
            Assert.Single(engine.ActiveProfile.Filters);

            DesktopSignalPathState? lastSignal = null;
            engine.SignalPathChanged += s => lastSignal = s;

            engine.Stop();

            Assert.Equal("Idle — Standby", engine.CurrentSignalPath.SignalPathBadge);
            Assert.NotNull(lastSignal);
            Assert.Equal("Idle — Standby", lastSignal!.SignalPathBadge);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
