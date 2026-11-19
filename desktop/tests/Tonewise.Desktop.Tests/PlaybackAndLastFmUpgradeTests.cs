using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class PlaybackAndLastFmUpgradeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root");
    }

    private static string CreateTestManifestDb(string tempRoot)
    {
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        string dbPath = Path.Combine(dotTonewise, "manifest.db");
        string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var setup = conn.CreateCommand();
        setup.CommandText = schemaSql;
        setup.ExecuteNonQuery();

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO tracks (
                track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                title, album, artists_json, rating
            ) VALUES
            (
                'uid_bwv988', X'01', 64, X'02', 512,
                'Bach/Goldberg/01_Aria.flac', 2048, 1000, 'flac', 96000, 185000,
                'Aria', 'Goldberg Variations', '["Glenn Gould"]', 5
            ),
            (
                'uid_short', X'03', 64, X'04', 512,
                'Short/Track.flac', 1024, 1000, 'flac', 44100, 18000,
                'Short Prelude', 'Short Set', '["Chopin"]', 0
            );
            """;
        ins.ExecuteNonQuery();
        new StagingDiffStore(dbPath).EnsureSchema();
        return dbPath;
    }

    [Fact]
    public void PlaybackScrobbleEvaluator_EnforcesLastFmThresholds_AndRejectsFakeSeek()
    {
        // Under 30s track: counts locally on natural completion >= 5s, NEVER Last.fm scrobble-eligible
        Assert.True(PlaybackScrobbleEvaluator.ShouldCommitPlay(20_000, 18_000, completedNaturally: true));
        Assert.False(PlaybackScrobbleEvaluator.IsEligibleForLastFmScrobble(20_000, 18_000, completedNaturally: true));

        // 180s track with 91s accumulated -> both commit and scrobble eligible
        Assert.True(PlaybackScrobbleEvaluator.ShouldCommitPlay(180_000, 91_000, completedNaturally: false));
        Assert.True(PlaybackScrobbleEvaluator.IsEligibleForLastFmScrobble(180_000, 91_000, completedNaturally: false));

        // 20-minute Mahler movement -> 4 minutes (240s) satisfies scrobble threshold even before 50%
        Assert.True(PlaybackScrobbleEvaluator.ShouldCommitPlay(1_200_000, 240_500, completedNaturally: false));
        Assert.True(PlaybackScrobbleEvaluator.IsEligibleForLastFmScrobble(1_200_000, 240_500, completedNaturally: false));

        // Negative case: seeking straight to end (only 500ms listened on a 180s track) must be rejected
        Assert.False(PlaybackScrobbleEvaluator.ShouldCommitPlay(180_000, 500, completedNaturally: true));
        Assert.False(PlaybackScrobbleEvaluator.IsEligibleForLastFmScrobble(180_000, 500, completedNaturally: true));
    }

    [Fact]
    public void RecordLocalPlaybackEvent_WritesDesktopHistory_UpdatesSqlite_AndFeedsScrobbleBatches()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_rec_lfm_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateTestManifestDb(tempRoot);
            string dotTonewise = Path.Combine(tempRoot, ".tonewise");
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            LastFmLedgerService.RecordLocalPlaybackEvent(
                dbPath,
                dotTonewise,
                "uid_bwv988",
                "Glenn Gould, J.S. Bach",
                "Aria",
                "Goldberg Variations",
                nowSec - 120,
                scrobbleEligible: true);

            // Sub-30s track updates play_count_total without queuing a scrobble
            LastFmLedgerService.RecordLocalPlaybackEvent(
                dbPath,
                dotTonewise,
                "uid_short",
                "Chopin",
                "Short Prelude",
                "Short Set",
                nowSec - 30,
                scrobbleEligible: false);

            var prep = LastFmLedgerService.PrepareScrobbleBatches(dbPath, dotTonewise, nowSec);
            Assert.Single(prep.Batches);
            Assert.Single(prep.Batches[0].Items);
            Assert.Equal("Glenn Gould", prep.Batches[0].Items[0].Artist);
            Assert.Equal("uid_bwv988", prep.Batches[0].Items[0].TrackUid);

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT play_count_total, last_played_ts FROM tracks WHERE track_uid = 'uid_bwv988';";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal((nowSec - 120) * 1000L, reader.GetInt64(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoveOutbox_DeduplicatesTransitions_AndFiveStarBackfillRespectsPendingUnloves()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_love_outbox_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateTestManifestDb(tempRoot);
            string dotTonewise = Path.Combine(tempRoot, ".tonewise");

            // Love -> Unlove -> Love collapses to a single Love op
            LastFmLedgerService.EnqueueLoveChange(dotTonewise, "uid_bwv988", "Glenn Gould, Bach", "Aria", isLoved: true, updatedAtUnixSec: 100);
            LastFmLedgerService.EnqueueLoveChange(dotTonewise, "uid_bwv988", "Glenn Gould", "Aria", isLoved: false, updatedAtUnixSec: 101);
            var opsAfterUnlove = LastFmLedgerService.ReadPendingLoveOps(dotTonewise);
            Assert.Single(opsAfterUnlove);
            Assert.False(opsAfterUnlove[0].IsLoved);

            // Backfill of local 5-star tracks must NOT overwrite a newer pending Unlove for uid_bwv988
            LastFmLedgerService.EnqueueAllLocalFiveStarAsLoved(dbPath, dotTonewise);
            var opsAfterBackfill = LastFmLedgerService.ReadPendingLoveOps(dotTonewise);
            Assert.Single(opsAfterBackfill);
            Assert.False(opsAfterBackfill[0].IsLoved);

            // Explicit re-love updates state to true
            LastFmLedgerService.EnqueueLoveChange(dotTonewise, "uid_bwv988", "Glenn Gould", "Aria", isLoved: true, updatedAtUnixSec: 102);
            var finalOps = LastFmLedgerService.ReadPendingLoveOps(dotTonewise);
            Assert.Single(finalOps);
            Assert.True(finalOps[0].IsLoved);
            Assert.Equal("Glenn Gould", finalOps[0].Artist);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LastFmWebApiClient_SupportsNowPlaying_LoveUnlove_BrowserOAuth_AndFlushesUnloveBeforePull()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_lfm_http_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateTestManifestDb(tempRoot);
            string dotTonewise = Path.Combine(tempRoot, ".tonewise");
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            LastFmLedgerService.EnqueueLoveChange(dotTonewise, "uid_bwv988", "Glenn Gould", "Aria", isLoved: false, nowSec);
            LastFmLedgerService.RecordLocalPlaybackEvent(dbPath, dotTonewise, "uid_bwv988", "Glenn Gould", "Aria", "Goldberg Variations", nowSec - 60, scrobbleEligible: true);

            var callLog = new List<string>();
            var handler = new StubHttpMessageHandler(req =>
            {
                string body = req.Content is not null ? req.Content.ReadAsStringAsync().GetAwaiter().GetResult() : (req.RequestUri?.Query ?? "");
                if (body.Contains("method=auth.getToken", StringComparison.Ordinal))
                {
                    callLog.Add("auth.getToken");
                    return StubJsonResponse("{\"token\":\"tok_12345\"}");
                }
                if (body.Contains("method=auth.getSession", StringComparison.Ordinal))
                {
                    callLog.Add("auth.getSession");
                    return StubJsonResponse("{\"session\":{\"name\":\"shawnshi\",\"key\":\"sk_abcde\"}}");
                }
                if (body.Contains("method=track.updateNowPlaying", StringComparison.Ordinal))
                {
                    callLog.Add("track.updateNowPlaying");
                    return StubJsonResponse("{\"nowplaying\":{}}");
                }
                if (body.Contains("method=track.unlove", StringComparison.Ordinal))
                {
                    callLog.Add("track.unlove");
                    return StubJsonResponse("{\"lfm\":{\"status\":\"ok\"}}");
                }
                if (body.Contains("method=track.love", StringComparison.Ordinal))
                {
                    callLog.Add("track.love");
                    return StubJsonResponse("{\"lfm\":{\"status\":\"ok\"}}");
                }
                if (body.Contains("method=track.scrobble", StringComparison.Ordinal))
                {
                    callLog.Add("track.scrobble");
                    return StubJsonResponse("{\"scrobbles\":{\"@attr\":{\"accepted\":1,\"ignored\":0}}}");
                }
                if (body.Contains("user.gettoptracks", StringComparison.Ordinal))
                {
                    callLog.Add("user.gettoptracks");
                    return StubJsonResponse("{\"toptracks\":{\"@attr\":{\"totalPages\":\"1\"},\"track\":[]}}");
                }
                if (body.Contains("user.getrecenttracks", StringComparison.Ordinal))
                {
                    callLog.Add("user.getrecenttracks");
                    return StubJsonResponse("{\"recenttracks\":{\"@attr\":{\"totalPages\":\"1\"},\"track\":[]}}");
                }
                if (body.Contains("user.getlovedtracks", StringComparison.Ordinal))
                {
                    callLog.Add("user.getlovedtracks");
                    return StubJsonResponse("{\"lovedtracks\":{\"@attr\":{\"totalPages\":\"1\"},\"track\":[]}}");
                }
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            });

            using var http = new HttpClient(handler);
            var client = new LastFmWebApiClient(http);

            var (token, url) = await client.RequestAuthTokenAsync("0123456789abcdef0123456789abcdef", "secret0123456789abcdef0123456789");
            Assert.Equal("tok_12345", token);
            Assert.Contains("tok_12345", url);

            var (sk, uname) = await client.ExchangeAuthTokenForSessionAsync("0123456789abcdef0123456789abcdef", "secret0123456789abcdef0123456789", token);
            Assert.Equal("sk_abcde", sk);
            Assert.Equal("shawnshi", uname);

            await client.UpdateNowPlayingAsync("0123456789abcdef0123456789abcdef", "secret0123456789abcdef0123456789", sk, "Glenn Gould", "Aria", "Goldberg Variations", 185);
            await client.PushTrackLoveStateAsync("0123456789abcdef0123456789abcdef", "secret0123456789abcdef0123456789", sk, "Glenn Gould", "Aria", isLoved: true);

            var cfg = new LastFmAccountConfig("0123456789abcdef0123456789abcdef", "secret0123456789abcdef0123456789", uname, sk, 0);
            var summary = await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, cfg);

            Assert.Equal(1, summary.PushedScrobbles);
            Assert.Equal(0, summary.PushedLoved);
            Assert.Equal(1, summary.PushedUnloved);
            Assert.Empty(LastFmLedgerService.ReadPendingLoveOps(dotTonewise));

            // Verify unlove was flushed BEFORE cloud getlovedtracks pull
            int idxUnlove = callLog.IndexOf("track.unlove");
            int idxPullLoved = callLog.IndexOf("user.getlovedtracks");
            Assert.True(idxUnlove >= 0 && idxPullLoved > idxUnlove);

            // Negative test: HTTP 200 containing Last.fm error 9 must throw and MUST NOT mark queued events as scrobbled
            LastFmLedgerService.RecordLocalPlaybackEvent(dbPath, dotTonewise, "uid_bwv988", "Glenn Gould", "Aria", "Goldberg Variations", nowSec - 10, scrobbleEligible: true);
            var errHandler = new StubHttpMessageHandler(_ => StubJsonResponse("{\"error\":9,\"message\":\"Invalid session key - Please re-authenticate\"}"));
            var errClient = new LastFmWebApiClient(new HttpClient(errHandler));
            await Assert.ThrowsAsync<InvalidOperationException>(() => errClient.FlushPendingScrobblesAsync(dbPath, dotTonewise, cfg));
            var stillPending = LastFmLedgerService.PrepareScrobbleBatches(dbPath, dotTonewise, nowSec);
            Assert.Single(stillPending.Batches);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WorkAwareShuffleEngine_PreservesClassicalMovementsInOrder_WithoutDuplicates()
    {
        var tracks = new List<ShuffleTrackDescriptor>
        {
            new("pop1", "Pop", "", null, "pop1.flac"),
            new("sym3", "Beethoven 9", "Symphony No. 9", 3, "03.flac"),
            new("sym1", "Beethoven 9", "Symphony No. 9", 1, "01.flac"),
            new("sym4", "Beethoven 9", "Symphony No. 9", 4, "04.flac"),
            new("sym2", "Beethoven 9", "Symphony No. 9", 2, "02.flac"),
            new("pop2", "Pop", "", null, "pop2.flac"),
        };

        // Anchor on sym2 (index 4): Symphony No. 9 block [sym1, sym2, sym3, sym4] must stay intact & ordered at the start!
        int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(tracks, anchorIndex: 4, new Random(1234));
        Assert.Equal(tracks.Count, order.Length);
        Assert.Equal(tracks.Count, order.Distinct().Count());
        Assert.Equal([2, 4, 1, 3], order[..4]);
    }

    [Fact]
    public void PlaybackSessionStore_RoundTripsSnapshot_AndHandlesCorruptJson()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_session_p0_" + Guid.NewGuid().ToString("N"));
        try
        {
            var snap = new PlaybackSessionSnapshot(
                ["u1", "u2", "u3"],
                "u2",
                94.5,
                "Late Night Focus",
                IsShuffled: true,
                RepeatMode: "all",
                ActiveSlotIndex: 2,
                SavedAtUnixMs: 1_763_300_000_000L);

            PlaybackSessionStore.Save(tempRoot, snap);
            var loaded = PlaybackSessionStore.Load(tempRoot);
            Assert.NotNull(loaded);
            Assert.Equal(["u1", "u2", "u3"], loaded!.QueueTrackUids);
            Assert.Equal("u2", loaded.CurrentTrackUid);
            Assert.Equal(94.5, loaded.PositionSeconds, 2);
            Assert.Equal(2, loaded.ActiveSlotIndex);

            // Corrupt JSON negative test returns null gracefully instead of throwing
            File.WriteAllText(PlaybackSessionStore.GetSessionFilePath(tempRoot), "{broken json");
            Assert.Null(PlaybackSessionStore.Load(tempRoot));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GaplessSampleProvider_TransitionsSeamlesslyOnMatchingFormat_AndRejectsMismatch()
    {
        float[] first = Enumerable.Repeat(0.25f, 100).ToArray();
        float[] second = Enumerable.Repeat(-0.5f, 100).ToArray();
        float[] incompatible = Enumerable.Repeat(0.1f, 60).ToArray();

        string? callbackPath = null;
        double callbackRg = 0.0;
        using var gapless = new GaplessSampleProvider(
            "first.flac",
            null,
            new TestArraySampleProvider(first, 48000, 2),
            null,
            onTransitionSync: (p, rg) =>
            {
                callbackPath = p;
                callbackRg = rg;
            });

        // Mismatched sample rate is rejected cleanly
        Assert.False(gapless.TryStageNext("bad.flac", null, new TestArraySampleProvider(incompatible, 44100, 2), null, -2.0));

        // Matching sample rate & channels is staged and pulled seamlessly in a single Read() call
        Assert.True(gapless.TryStageNext("second.flac", null, new TestArraySampleProvider(second, 48000, 2), null, -4.2));

        float[] outBuf = new float[200];
        int read = gapless.Read(outBuf, 0, outBuf.Length);
        Assert.Equal(200, read);
        Assert.Equal("second.flac", callbackPath);
        Assert.Equal(-4.2, callbackRg, 4);
        Assert.Equal(0.25f, outBuf[99]);
        Assert.Equal(-0.5f, outBuf[100]);
        Assert.False(gapless.ReachedEndOfStream);

        // Next read exhausts cleanly
        Assert.Equal(0, gapless.Read(outBuf, 0, 16));
        Assert.True(gapless.ReachedEndOfStream);
    }

    [Fact]
    public void ReplayGainResolver_AppliesAllFourModes_AndPreAmps()
    {
        Assert.Equal(0.0, ReplayGainResolver.ResolveGainDb("off", -5.0, -7.0, isAlbumContext: true, 2.0, -3.0), 6);
        Assert.Equal(-3.0, ReplayGainResolver.ResolveGainDb("track", -5.0, -7.0, isAlbumContext: true, 2.0, -3.0), 6);
        Assert.Equal(-5.0, ReplayGainResolver.ResolveGainDb("album", -5.0, -7.0, isAlbumContext: false, 2.0, -3.0), 6);
        Assert.Equal(-5.0, ReplayGainResolver.ResolveGainDb("dynamic", -5.0, -7.0, isAlbumContext: true, 2.0, -3.0), 6);
        Assert.Equal(-3.0, ReplayGainResolver.ResolveGainDb("dynamic", -5.0, -7.0, isAlbumContext: false, 2.0, -3.0), 6);
        Assert.Equal(-3.0, ReplayGainResolver.ResolveGainDb("dynamic", null, null, isAlbumContext: false, 2.0, -3.0), 6);
    }

    [Fact]
    public void DspSampleProvider_ExtendedStages_HandleCrossfeed_Balance_Mono_Limiter_AndGainMatchedBypass()
    {
        const int frames = 2048;
        float[] leftOnly = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            leftOnly[i * 2] = 0.8f * MathF.Sin(2f * MathF.PI * 200f * i / 48000f);
            leftOnly[i * 2 + 1] = 0f;
        }

        // 1. Bauer Crossfeed bleeds low-frequency Left signal into Right channel
        var crossDsp = new DspSampleProvider(new TestArraySampleProvider(leftOnly, 48000, 2));
        crossDsp.ConfigureDither(false);
        crossDsp.ConfigureExtendedDsp(false, null, crossfeedEnabled: true, 700.0, 4.5, 0.28, balanceDb: 0.0, monoFold: false, limiterEnabled: true);
        float[] crossOut = new float[leftOnly.Length];
        crossDsp.Read(crossOut, 0, crossOut.Length);
        double rightBleedPeak = 0.0;
        for (int i = 200; i < frames; i++) rightBleedPeak = Math.Max(rightBleedPeak, Math.Abs(crossOut[i * 2 + 1]));
        Assert.True(rightBleedPeak > 0.1, $"Expected crossfeed bleed into Right channel, got {rightBleedPeak:F4}");

        // 2. Mono Fold merges L & R to identical signal
        var monoDsp = new DspSampleProvider(new TestArraySampleProvider(leftOnly, 48000, 2));
        monoDsp.ConfigureDither(false);
        monoDsp.ConfigureExtendedDsp(false, null, crossfeedEnabled: false, 700.0, 4.5, 0.28, balanceDb: 0.0, monoFold: true, limiterEnabled: false);
        float[] monoOut = new float[leftOnly.Length];
        monoDsp.Read(monoOut, 0, monoOut.Length);
        Assert.Equal(monoOut[500], monoOut[501], 5);

        // 3. 3ms Lookahead Limiter protects against heavy positive overload (+12 dB gain)
        var hotSamples = Enumerable.Repeat(0.95f, 1024).ToArray();
        var limDsp = new DspSampleProvider(new TestArraySampleProvider(hotSamples, 48000, 2));
        limDsp.ConfigureDither(false);
        limDsp.Configure(null, 12.0);
        limDsp.ConfigureExtendedDsp(false, null, false, 700.0, 4.5, 0.28, 0.0, false, limiterEnabled: true, limiterCeilingDb: -0.2);
        float[] limOut = new float[hotSamples.Length];
        limDsp.Read(limOut, 0, limOut.Length);
        Assert.All(limOut.Skip(256), s => Assert.InRange(Math.Abs(s), 0.0f, 1.0f));

        // 4. Gain-Matched A/B Bypass strips EQ notch coloration while preserving RMS loudness compensation
        var preset = DesktopDspChainConfig.CreatePreset(DesktopDspPresetId.OpenEarBluetooth);
        double estShift = DesktopDspChainConfig.EstimateRmsLoudnessShiftDb(preset.ToEqualizerApoProfile(), 48000);
        Assert.True(estShift < 0.5);

        const int toneFrames = 4096;
        float[] testTone = new float[toneFrames * 2];
        for (int i = 0; i < toneFrames; i++)
        {
            float s = 0.25f * MathF.Sin(2f * MathF.PI * 1000f * i / 48000f);
            testTone[i * 2] = s;
            testTone[i * 2 + 1] = s;
        }
        var deepCut = new EqualizerApoProfile(0.0, [new BiquadSpec(BiquadKind.Peaking, 1000.0, -12.0, 2.0)]);
        var abActive = new DspSampleProvider(new TestArraySampleProvider(testTone, 48000, 2));
        abActive.ConfigureDither(false);
        abActive.Configure(deepCut, 0.0);
        abActive.ConfigureExtendedDsp(gainMatchedBypass: false, null, false, 700, 4.5, 0.28, 0.0, false, false);
        float[] eqBuf = new float[testTone.Length];
        abActive.Read(eqBuf, 0, eqBuf.Length);

        var abBypass = new DspSampleProvider(new TestArraySampleProvider(testTone, 48000, 2));
        abBypass.ConfigureDither(false);
        abBypass.Configure(deepCut, 0.0);
        abBypass.ConfigureExtendedDsp(gainMatchedBypass: true, 0.0, false, 700, 4.5, 0.28, 0.0, false, false);
        float[] bypassBuf = new float[testTone.Length];
        abBypass.Read(bypassBuf, 0, bypassBuf.Length);

        double eqRms = Math.Sqrt(eqBuf.Skip(2048).Average(x => (double)x * x));
        double bypassRms = Math.Sqrt(bypassBuf.Skip(2048).Average(x => (double)x * x));
        Assert.True(bypassRms > eqRms * 2.5, $"Expected Gain-Matched A/B Bypass ({bypassRms:F4}) to restore cut EQ level ({eqRms:F4})");
    }

    private static HttpResponseMessage StubJsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class TestArraySampleProvider(float[] data, int sampleRate, int channels) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int rem = Math.Min(count, data.Length - _pos);
            if (rem <= 0) return 0;
            Array.Copy(data, _pos, buffer, offset, rem);
            _pos += rem;
            return rem;
        }
    }
}
