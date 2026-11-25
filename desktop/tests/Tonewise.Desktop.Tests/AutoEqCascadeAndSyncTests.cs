using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class AutoEqCascadeAndSyncTests
{
    private sealed class FiniteSineSampleProvider : ISampleProvider, IDisposable
    {
        private int _remainingFrames;
        private readonly float _amplitude;
        private int _phase;

        public FiniteSineSampleProvider(int sampleRate, int channels, int remainingFrames, float amplitude = 0.4f)
        {
            _remainingFrames = remainingFrames;
            _amplitude = amplitude;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int maxFrames = Math.Min(_remainingFrames, count / channels);
            if (maxFrames <= 0) return 0;

            for (int f = 0; f < maxFrames; f++)
            {
                float s = (float)(_amplitude * Math.Sin(2.0 * Math.PI * 440.0 * _phase++ / WaveFormat.SampleRate));
                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + f * channels + c] = s;
                }
            }
            _remainingFrames -= maxFrames;
            return maxFrames * channels;
        }

        public void Dispose() { }
    }

    [Fact]
    public void CoreFfi_ResolvesClassicalFlatWithCrossfeed_WorkLock_AndComposesAntiClippingHeadroom()
    {
        var catalog = TonewiseCoreClient.GetDefaultAutoEqCatalog();
        Assert.Equal(24, catalog.Count);

        var policy = new AutoEqPolicyDto(
            Enabled: true,
            ClassicalAutoCrossfeed: true,
            LockWithinClassicalWork: true,
            UpdatedAtMs: 100);

        var mov1 = new TrackAutoEqInputDto(
            Genres: ["Classical"],
            DjVocals: "instrumental",
            WorkName: "Symphony No. 9",
            WorkKey: "beethoven9");
        var res1 = TonewiseCoreClient.ResolveAutoEq(policy, mov1);
        Assert.True(res1.Applied);
        Assert.Equal("classical", res1.GenreFamily);
        Assert.Equal("instrumental", res1.VocalClass);
        Assert.False(res1.HasNonZeroEq);
        Assert.True(res1.EnableCrossfeed);
        Assert.Equal(10, res1.Filters.Count);
        Assert.All(res1.Filters, f => Assert.InRange(Math.Abs(f.GainDb), 0f, 1e-5f));

        // 4th movement has chorus ("vocal"), but lock_within_classical_work retains Movement 1 profile
        var mov4 = new TrackAutoEqInputDto(
            Genres: ["Classical; Choral"],
            DjVocals: "vocal",
            WorkName: "Symphony No. 9",
            WorkKey: "beethoven9");
        var res4 = TonewiseCoreClient.ResolveAutoEq(policy, mov4, "beethoven9", res1);
        Assert.True(res4.LockedToPreviousWork);
        Assert.False(res4.HasNonZeroEq);
        Assert.True(res4.EnableCrossfeed);

        // Two-layer cascade: Layer A (OpenEar +4dB @ 62 Hz) + Layer B (custom +3dB @ 62 Hz) prevents clipping
        var layerA = new DspProfileDto(
            PreampDb: 0f,
            Filters: [new AutoEqBiquadDto("LowShelf", 62f, 4.0f, 0.71f)]);
        var layerB = new DspProfileDto(
            PreampDb: 0f,
            Filters: [new AutoEqBiquadDto("LowShelf", 62f, 3.0f, 0.71f)]);
        var composed = TonewiseCoreClient.ComposeDsp(layerA, layerB);
        Assert.Equal(2, composed.Filters.Count);
        Assert.True(composed.PreampDb <= -6.8f, $"Expected anti-clipping preamp <= -6.8 dB, got {composed.PreampDb}");
    }

    [Fact]
    public void BiquadFilter64_RampsCoefficientsSmoothly_AndZeroBandsKeepIdentityPassthrough()
    {
        var source = new FiniteSineSampleProvider(48000, 2, 2048);
        var dsp = new DspSampleProvider(source);

        var twentyZeroBands = Enumerable.Range(0, 20)
            .Select(i => new BiquadSpec(BiquadKind.Peaking, 31.0 + i * 200.0, 0.0, 1.41))
            .ToList();
        dsp.Configure(new EqualizerApoProfile(0.0, twentyZeroBands), replayGainDb: 0.0);
        Assert.True(dsp.IsIdentityPassthrough);

        // Step 1: non-zero boost
        var boostBands = twentyZeroBands.ToList();
        boostBands[1] = new BiquadSpec(BiquadKind.LowShelf, 62.0, 4.0, 0.71);
        dsp.Configure(new EqualizerApoProfile(-4.0, boostBands), replayGainDb: 0.0);
        Assert.False(dsp.IsIdentityPassthrough);

        float[] buf = new float[1024];
        int read1 = dsp.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read1);
        Assert.All(buf, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));

        // Step 2: hot-update coefficients (same 20-band count triggers 15 ms smooth ramp)
        boostBands[1] = new BiquadSpec(BiquadKind.LowShelf, 62.0, -3.5, 0.71);
        dsp.Configure(new EqualizerApoProfile(0.0, boostBands), replayGainDb: 0.0);

        int read2 = dsp.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read2);
        Assert.All(buf, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));
    }

    [Fact]
    public void GaplessSampleProvider_EmitsComposedDspTransitionOnExactBoundary()
    {
        GaplessDspTransition? observed = null;
        var trackA = new FiniteSineSampleProvider(44100, 2, 128);
        var trackB = new FiniteSineSampleProvider(44100, 2, 128);

        using var gapless = new GaplessSampleProvider(
            "a.flac",
            null,
            trackA,
            trackA,
            onTransitionSync: (_, transition) => observed = transition);

        var nextEq = new EqualizerApoProfile(-1.5, [new BiquadSpec(BiquadKind.Peaking, 2000.0, 1.5, 1.2)]);
        bool staged = gapless.TryStageNext(
            "b.flac",
            null,
            trackB,
            trackB,
            new GaplessDspTransition(-2.25, nextEq, HasProfileOverride: true, CrossfeedEnabled: true));
        Assert.True(staged);

        float[] buf = new float[512];
        int first = gapless.ReadSegment(buf, 0, buf.Length);
        Assert.Equal(256, first);
        Assert.Null(observed);

        int second = gapless.ReadSegment(buf, 0, buf.Length);
        Assert.Equal(256, second);
        Assert.NotNull(observed);
        Assert.True(observed!.HasProfileOverride);
        Assert.Equal(-2.25, observed.ReplayGainDb, 4);
        Assert.Equal(true, observed.CrossfeedEnabled);
        Assert.Single(observed.ComposedProfile!.Filters);
    }

    [Fact]
    public void SyncHostServer_AutoEqPolicyLwwMergePrefersNewerTimestamp()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_autoeq_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var host = new SyncHostServer(tempRoot);
            var local = new AutoEqPolicyDto(
                Enabled: true,
                ClassicalAutoCrossfeed: true,
                LockWithinClassicalWork: true,
                UpdatedAtMs: 5000,
                CustomRules: new Dictionary<string, AutoEqRuleOverrideDto>
                {
                    ["jazz:vocal"] = new(0f, [new AutoEqBiquadDto("Peaking", 2000f, 2.0f, 1.41f)], null),
                });
            host.WriteAutoEqPolicy(local);

            // Stale remote policy (UpdatedAtMs = 4000) must not overwrite local
            var staleRemote = local with { UpdatedAtMs = 4000, Enabled = false };
            var (winner1, changed1) = host.MergeAutoEqPolicyWithChangeFlag(staleRemote);
            Assert.False(changed1);
            Assert.True(winner1.Enabled);
            Assert.Equal(5000UL, winner1.UpdatedAtMs);

            // Newer remote policy (UpdatedAtMs = 9000) must win and persist to .tonewise/auto_eq_policy.json
            var newerRemote = local with
            {
                UpdatedAtMs = 9000,
                ClassicalAutoCrossfeed = false,
                CustomRules = new Dictionary<string, AutoEqRuleOverrideDto>
                {
                    ["rock_metal:vocal"] = new(0f, [new AutoEqBiquadDto("LowShelf", 62f, 2.5f, 0.71f)], true),
                },
            };
            var (winner2, changed2) = host.MergeAutoEqPolicyWithChangeFlag(newerRemote);
            Assert.True(changed2);
            Assert.False(winner2.ClassicalAutoCrossfeed);
            Assert.True(winner2.CustomRules!.ContainsKey("rock_metal:vocal"));

            var reloaded = host.ReadAutoEqPolicy();
            Assert.Equal(9000UL, reloaded.UpdatedAtMs);
            Assert.False(reloaded.ClassicalAutoCrossfeed);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
