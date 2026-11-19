using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DesktopPlaybackAndClockP2Tests
{
    private sealed class FixedArraySampleProvider(float[] samples) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int rem = samples.Length - _pos;
            if (rem <= 0) return 0;
            int n = Math.Min(rem, count);
            Array.Copy(samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    [Fact]
    public void SleepTimerEngine_CountdownFadesLastTenSecondsAndExpires()
    {
        var engine = new SleepTimerEngine();
        var t0 = new DateTime(2026, 11, 19, 22, 0, 0, DateTimeKind.Utc);
        engine.SetCountdown(15, t0);

        var mid = engine.Tick(t0.AddMinutes(5));
        Assert.True(mid.IsActive);
        Assert.False(mid.Expired);
        Assert.Equal(1.0, mid.FadeGainLinear, 5);
        Assert.Equal("🌙 10:00", mid.BadgeText);

        var fade = engine.Tick(t0.AddMinutes(14).AddSeconds(55));
        Assert.True(fade.IsActive);
        Assert.False(fade.Expired);
        Assert.InRange(fade.FadeGainLinear, 0.49, 0.51);

        var expired = engine.Tick(t0.AddMinutes(15));
        Assert.False(expired.IsActive);
        Assert.True(expired.Expired);
        Assert.Equal(0.0, expired.FadeGainLinear, 5);
        Assert.Equal(SleepTimerMode.Off, engine.Mode);
    }

    [Fact]
    public void SleepTimerEngine_EndOfWorkStopsOnlyWhenExitingClassicalWorkBlock()
    {
        var engine = new SleepTimerEngine();
        engine.SetEndOfWork("berlin phil|karajan::symphony no. 5");

        Assert.False(engine.ShouldStopAtTrackTransition(
            "berlin phil|karajan::symphony no. 5",
            "berlin phil|karajan::symphony no. 5"));
        Assert.Equal(SleepTimerMode.EndOfWork, engine.Mode);

        Assert.True(engine.ShouldStopAtTrackTransition(
            "berlin phil|karajan::symphony no. 5",
            "vienna phil|kleiber::symphony no. 7"));
        Assert.Equal(SleepTimerMode.Off, engine.Mode);

        engine.SetEndOfTrack();
        Assert.True(engine.ShouldStopAtTrackTransition("a", "a"));
        Assert.Equal(SleepTimerMode.Off, engine.Mode);
    }

    [Fact]
    public void EqualizerApoProfile_ParsesNumberedFilterLines()
    {
        const string apo = """
            Preamp: -3.0 dB
            Filter 1: ON PK Fc 1000 Hz Gain 2.5 dB Q 1.41
            Filter 2: ON LSC Fc 80 Hz Gain 3.0 dB Q 0.71
            FilterBank: ON PK Fc 500 Hz Gain 1.0 dB Q 1.0
            """;
        var profile = EqualizerApoProfile.Parse(apo);
        Assert.Equal(-3.0, profile.PreampDb, 4);
        Assert.Equal(2, profile.Filters.Count);
        Assert.Equal(BiquadKind.Peaking, profile.Filters[0].Kind);
        Assert.Equal(BiquadKind.LowShelf, profile.Filters[1].Kind);
    }

    [Fact]
    public void MasterVolume_ZeroPercentProducesExactDigitalSilence()
    {
        var dsp = new DspSampleProvider(new FixedArraySampleProvider([0.8f, -0.8f, 0.5f, -0.5f]))
        {
            IsTpdfDitherEnabled = false,
        };
        dsp.ConfigureMasterVolume(-80.0, muted: false);
        Assert.Equal(0.0, dsp.MasterLinearGain, 10);

        var outBuf = new float[4];
        int n = dsp.Read(outBuf, 0, outBuf.Length);
        Assert.Equal(4, n);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0f, outBuf[i]);
        }
    }

    private sealed record ShuffleItem(string Uid, string Album, string? Work, string Conductor);

    [Fact]
    public void AvoidLeadingRepeatPreservingWorkBlock_SkipsWholeWorkWhenPreviousTrackWasFinalMovement()
    {
        var list = new List<ShuffleItem>
        {
            new("m1", "Beethoven", "Sym 5", "Karajan"),
            new("m2", "Beethoven", "Sym 5", "Karajan"),
            new("m3", "Beethoven", "Sym 5", "Karajan"),
            new("o1", "Encores", null, ""),
            new("o2", "Encores", null, ""),
        };

        string? prevWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            "Sym 5",
            "Beethoven",
            WorkAwareShuffleEngine.BuildAlbumScope("Beethoven", "Karajan"));

        WorkAwareShuffleEngine.AvoidLeadingRepeatPreservingWorkBlock(
            list,
            previousTrackUid: "m3",
            x => x.Uid,
            x => x.Album,
            x => x.Work,
            x => WorkAwareShuffleEngine.BuildAlbumScope(x.Album, x.Conductor),
            previousWorkKey: prevWorkKey);

        Assert.Equal(["o1", "o2", "m1", "m2", "m3"], list.Select(x => x.Uid).ToArray());
    }
}
