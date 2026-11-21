using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Audio;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class Batch2ProtocolAndParityTests
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

    private static string CreateManifestDb(string tempRoot)
    {
        string dotDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotDir);
        string dbPath = Path.Combine(dotDir, "manifest.db");
        string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaSql;
        cmd.ExecuteNonQuery();
        new StagingDiffStore(dbPath).EnsureSchema();
        return dbPath;
    }

    private static async Task<string> PairHttpClientAsync(HttpClient http, SyncHostServer server, string deviceId = "pixel-b2")
    {
        string code = server.Store.CurrentPairingCode;
        var resp = await http.PostAsJsonAsync("/api/v2/pair", new
        {
            code,
            device_id = deviceId,
            device_name = "Batch2 Phone"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string token = doc.RootElement.GetProperty("token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    [Fact]
    public async Task ReportV2_LwwProtocol_NormalizesValues_RejectsInvalidEnum_UpdatesLoveOutbox_AndBlocksTitleOnlyCollision()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b2_lww_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateManifestDb(tempRoot);
            string dotTonewise = Path.Combine(tempRoot, ".tonewise");
            string uidA = new string('a', 64);
            string uidB = new string('b', 64);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, artists_json, dj_vocals, dj_vocals_ts, rating, rating_ts
                    ) VALUES
                    ($uidA, X'0102', 64, X'0304', 0, 'ArtistA/01.flac', 1000, 1000, 'flac', 44100, 180000,
                     'Dawn Reflected', 'AlbumA', '["Artist A"]', 'vocal', 100, 0, 0),
                    ($uidB, X'0506', 64, X'0708', 0, 'ArtistB/02.flac', 1000, 1000, 'flac', 44100, 180000,
                     'Hallelujah', 'CoverAlbum', '["Leonard Cohen"]', 'vocal', 100, 0, 100);
                    """;
                ins.Parameters.AddWithValue("$uidA", uidA);
                ins.Parameters.AddWithValue("$uidB", uidB);
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-b2-lww");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            // 1. Apply valid LWW mutations via lww_ts_ms + "tempo" alias + empty-to-NULL + rating=5 love + invalid enum + wrong-artist cover title
            var report1 = await http.PostAsJsonAsync("/api/v2/report", new
            {
                device_id = "pixel-b2",
                play_events = new object[]
                {
                    new
                    {
                        event_id = "ev_cover_mismatch",
                        track_uid = "unmatched_local_id",
                        artist = "Jeff Buckley",
                        title = "Hallelujah",
                        play_count_delta = 3,
                        last_played_ts_ms = 1_763_300_000_000L,
                        device_id = "pixel-b2"
                    }
                },
                mutations = new object[]
                {
                    new { mutation_key = "m_tempo", track_uid = uidA, field = "tempo", value = "fast", lww_ts_ms = 2000L, device_id = "pixel-b2" },
                    new { mutation_key = "m_clear_vocals", track_uid = uidA, field = "dj_vocals", value = "", lww_ts_ms = 2000L, device_id = "pixel-b2" },
                    new { mutation_key = "m_clear_mood", track_uid = uidA, field = "mood", value = "", lww_ts_ms = 2000L, device_id = "pixel-b2" },
                    new { mutation_key = "m_invalid_energy", track_uid = uidA, field = "dj_energy", value = "bogus_energy", lww_ts_ms = 2000L, device_id = "pixel-b2" },
                    new { mutation_key = "m_love_a", track_uid = uidA, field = "rating", value = 5, lww_ts_ms = 2000L, device_id = "pixel-b2" },
                    new { mutation_key = "m_cover_wrong_artist", track_uid = "unmatched_cover", artist = "Jeff Buckley", title = "Hallelujah", field = "rating", value = 5, lww_ts_ms = 2000L, device_id = "pixel-b2" },
                },
                completed_outbox_ids = Array.Empty<string>()
            });
            Assert.Equal(HttpStatusCode.OK, report1.StatusCode);

            using (var doc1 = JsonDocument.Parse(await report1.Content.ReadAsStringAsync()))
            {
                var root1 = doc1.RootElement;
                Assert.True(root1.GetProperty("ok").GetBoolean());
                Assert.Equal(0, root1.GetProperty("merged_tracks").GetInt32()); // Title-only fallback blocked across artists!
                var acks = root1.GetProperty("acknowledged_mutation_keys").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.Contains("m_tempo", acks);
                Assert.Contains("m_clear_vocals", acks);
                Assert.Contains("m_clear_mood", acks);
                Assert.Contains("m_love_a", acks);
                Assert.DoesNotContain("m_invalid_energy", acks);

                var unmatched = root1.GetProperty("unmatched_mutation_uids").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("unmatched_cover", unmatched);

                var loved = root1.GetProperty("loved_uids").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.Contains(uidA, loved);

                // uidB has rating_ts = 100 > 0 and rating = 0 (< 5) -> appears in unloved_uids!
                var unloved = root1.GetProperty("unloved_uids").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.Contains(uidB, unloved);
            }

            // Verify SQLite columns & pending Last.fm love outbox
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT tempo_bucket, tempo_ts, dj_vocals, mood_json, rating, dj_energy FROM tracks WHERE track_uid = $uid;";
                q.Parameters.AddWithValue("$uid", uidA);
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Fast", r.GetString(0));
                Assert.Equal(2000L, r.GetInt64(1));
                Assert.True(r.IsDBNull(2)); // empty string -> SQL NULL on nullable enum column
                Assert.Equal("[]", r.GetString(3)); // NOT NULL JSON column remains "[]"
                Assert.Equal(5, r.GetInt32(4));
                Assert.True(r.IsDBNull(5)); // bogus_energy rejected cleanly
            }

            var loveOps1 = LastFmLedgerService.ReadPendingLoveOps(dotTonewise);
            Assert.Contains(loveOps1, op => op.TrackUid == uidA && op.IsLoved);

            // 2. Unlove uidA via rating=0 -> updates SQLite, sets Last.fm unlove op, and surfaces in unloved_uids
            var report2 = await http.PostAsJsonAsync("/api/v2/report", new
            {
                device_id = "pixel-b2",
                play_events = Array.Empty<object>(),
                mutations = new object[]
                {
                    new { mutation_key = "m_unlove_a", track_uid = uidA, field = "rating", value = 0, updated_at_ms = 3000L, device_id = "pixel-b2" },
                },
                completed_outbox_ids = Array.Empty<string>()
            });
            report2.EnsureSuccessStatusCode();
            using (var doc2 = JsonDocument.Parse(await report2.Content.ReadAsStringAsync()))
            {
                var unloved2 = doc2.RootElement.GetProperty("unloved_uids").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.Contains(uidA, unloved2);
            }
            var loveOps2 = LastFmLedgerService.ReadPendingLoveOps(dotTonewise);
            Assert.Contains(loveOps2, op => op.TrackUid == uidA && !op.IsLoved);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PlaylistMerge_PreservesExtinfUnmatched_HandlesPcDelete_NormalizesPaths_AndUnpairCleansOutbox()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b2_pl_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateManifestDb(tempRoot);
            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);

            // PC-side playlist with #EXTINF & #EXTTW-UNMATCHED preserved
            string m3uPath = Path.Combine(playlistsDir, "My Jazz Mix.m3u8");
            File.WriteAllText(m3uPath, """
                #EXTM3U
                #PLAYLIST:My Jazz Mix
                #EXTINF:215,Miles Davis - Blue in Green
                ../Tracks/Jazz/01.flac
                #EXTINF:190,Unmatched Rare Track
                #EXTTW-UNMATCHED:Rare/02.flac
                """, new UTF8Encoding(false));

            await using var server = new SyncHostServer(tempRoot, "pc-b2-pl", allowPhonePlaylistUpload: true);
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server, "pixel-pl");

            var mergeResp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "My Jazz Mix",
                base_list = Array.Empty<string>(),
                @base = new[] { "../Tracks/Jazz/01.flac" },
                ours = new[] { "../Tracks/Jazz/01.flac" },
                theirs = new[] { "Music/Tonewise/Jazz/01.flac", "../Jazz/03.flac" },
            });
            mergeResp.EnsureSuccessStatusCode();
            using (var doc = JsonDocument.Parse(await mergeResp.Content.ReadAsStringAsync()))
            {
                var merged = doc.RootElement.GetProperty("merged").EnumerateArray().Select(e => e.GetString()!).ToList();
                Assert.Equal(["Jazz/01.flac", "Jazz/03.flac"], merged);
                Assert.False(doc.RootElement.GetProperty("deleted").GetBoolean());
            }

            string written = File.ReadAllText(m3uPath, Encoding.UTF8);
            Assert.Contains("#EXTINF:215,Miles Davis - Blue in Green", written);
            Assert.Contains("#EXTTW-UNMATCHED:Rare/02.flac", written);
            Assert.Contains("Jazz/03.flac", written);

            // PC deletes a known playlist ("Old Mix") -> missing file on PC + theirs == base must return deleted=true and NOT resurrect
            var delResp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "Old Mix",
                @base = new[] { "Jazz/01.flac" },
                ours = new[] { "Jazz/01.flac" },
                theirs = new[] { "Jazz/01.flac" },
            });
            delResp.EnsureSuccessStatusCode();
            using (var delDoc = JsonDocument.Parse(await delResp.Content.ReadAsStringAsync()))
            {
                Assert.True(delDoc.RootElement.GetProperty("deleted").GetBoolean());
                Assert.Empty(delDoc.RootElement.GetProperty("merged").EnumerateArray());
            }
            Assert.False(File.Exists(Path.Combine(playlistsDir, "Old Mix.m3u8")));

            // Verify SanitizePlaylistFileName keeps spaces and replaces forbidden chars with underscores
            Assert.Equal("Late Night_ Jazz 2026", PlaylistStore.SanitizePlaylistFileName("  Late Night: Jazz   2026  "));

            // Verify UnpairDevice removes device-specific outbox items while keeping broadcast "*"
            server.Store.EnqueuePlaylist("For Phone Only", deviceId: "pixel-pl");
            server.Store.EnqueuePlaylist("Broadcast Mix", deviceId: "*");
            Assert.True(server.Store.UnpairDevice("pixel-pl"));
            var remainingBroadcast = server.Store.GetOutboxItems(deviceId: "other-device");
            Assert.Contains(remainingBroadcast, i => i.TargetKey == "Broadcast Mix" && i.DeviceId == "*");
            var afterUnpairForOldPhone = server.Store.GetOutboxItems(deviceId: "pixel-pl");
            Assert.DoesNotContain(afterUnpairForOldPhone, i => i.TargetKey == "For Phone Only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StatusEndpoint_ReturnsWaveformsSha256_AndDefaultScenesMatchProtocolSsot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b2_status_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateManifestDb(tempRoot);
            byte[] wfBytes = Encoding.UTF8.GetBytes("TWWF_BATCH2_SYNTHETIC_WAVEFORM_PAYLOAD");
            File.WriteAllBytes(Path.Combine(tempRoot, ".tonewise", "waveforms.bin"), wfBytes);
            string expectedSha = Convert.ToHexStringLower(SHA256.HashData(wfBytes));

            await using var server = new SyncHostServer(tempRoot, "pc-b2-status");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var resp = await http.GetAsync("/api/v1/status");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("has_waveforms").GetBoolean());
            Assert.Equal(expectedSha, doc.RootElement.GetProperty("waveforms_sha256").GetString());

            // SSOT check: embedded default scenes match protocol/dictionaries/default_scenes_5d.json
            string ssotPath = Path.Combine(FindRepoRoot(), "protocol", "dictionaries", "default_scenes_5d.json");
            using var ssotDoc = JsonDocument.Parse(File.ReadAllText(ssotPath, Encoding.UTF8));
            using var desktopDoc = JsonDocument.Parse(SyncHostServer.DefaultScenesJson());

            var ssotScenes = ssotDoc.RootElement.GetProperty("scenes").EnumerateArray()
                .Select(s => (Id: s.GetProperty("id").GetString(), SortArc: s.GetProperty("sort_arc").GetString()))
                .ToList();
            var desktopScenes = desktopDoc.RootElement.GetProperty("scenes").EnumerateArray()
                .Select(s => (Id: s.GetProperty("id").GetString(), SortArc: s.GetProperty("sort_arc").GetString()))
                .ToList();
            Assert.Equal(6, ssotScenes.Count);
            Assert.Equal(ssotScenes, desktopScenes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SharedCoreParity_AutoDjBpmBuckets_ArtistWhitelist_WorkShuffle_AndDspBufferContinuity()
    {
        // 1. AutoDJ BPM bucket centers match Rust tonewise-rules (58, 78, 105, 135, 160)
        static AutoDjTrackCandidate Cand(string uid, string bucket) => new(
            TrackUid: uid,
            Title: uid,
            Artist: $"Artist_{uid}",
            EffectivePlayCount: 5,
            Rating: 4,
            TempoBpm: null,
            TempoBucket: bucket,
            DjEnergy: "medium",
            DjVocals: "instrumental",
            DjScene: ["focus"],
            Moods: []);

        var noon = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.Equal(20.0, AutoDjPlaylistGenerator.Generate([Cand("a", "Very Slow"), Cand("b", "Slow")], noon, targetCount: 2, randomSeed: 1).MaxAdjacentBpmDelta);
        Assert.Equal(27.0, AutoDjPlaylistGenerator.Generate([Cand("a", "Slow"), Cand("b", "Moderate")], noon, targetCount: 2, randomSeed: 1).MaxAdjacentBpmDelta);
        Assert.Equal(30.0, AutoDjPlaylistGenerator.Generate([Cand("a", "Moderate"), Cand("b", "Fast")], noon, targetCount: 2, randomSeed: 1).MaxAdjacentBpmDelta);
        Assert.Equal(25.0, AutoDjPlaylistGenerator.Generate([Cand("a", "Fast"), Cand("b", "Very Fast")], noon, targetCount: 2, randomSeed: 1).MaxAdjacentBpmDelta);

        // 2. All 20 whitelisted artists from protocol/dictionaries/artist_whitelist.json preserved intact
        string wlPath = Path.Combine(FindRepoRoot(), "protocol", "dictionaries", "artist_whitelist.json");
        using var wlDoc = JsonDocument.Parse(File.ReadAllText(wlPath, Encoding.UTF8));
        var expectedNames = wlDoc.RootElement.GetProperty("protected_names").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
        Assert.Equal(20, expectedNames.Count);
        foreach (string name in expectedNames)
        {
            Assert.Equal([name], ArtistEntitySplitter.SplitPerformers(name));
            Assert.Equal([name, "Guest Artist"], ArtistEntitySplitter.SplitPerformers($"{name} / Guest Artist"));
        }

        // 3. WorkAwareShuffleEngine.AvoidLeadingRepeatPreservingWorkBlock moves whole work block without tearing movement 1
        var mvt1 = new ShuffleTrackDescriptor("w1_m1", "BWV 988", "Goldberg", 1, "01.flac");
        var mvt2 = new ShuffleTrackDescriptor("w1_m2", "BWV 988", "Goldberg", 2, "02.flac");
        var mvt3 = new ShuffleTrackDescriptor("w1_m3", "BWV 988", "Goldberg", 3, "03.flac");
        var standalone = new ShuffleTrackDescriptor("solo", "Kind of Blue", "", null, "04.flac");
        var ordered = new List<ShuffleTrackDescriptor> { mvt1, mvt2, mvt3, standalone };
        WorkAwareShuffleEngine.AvoidLeadingRepeatPreservingWorkBlock(
            ordered,
            previousTrackUid: "w1_m1",
            uidSelector: x => x.TrackUid,
            albumSelector: x => x.Album,
            workSelector: x => x.WorkName);
        Assert.Equal(["solo", "w1_m1", "w1_m2", "w1_m3"], ordered.Select(x => x.TrackUid).ToList());

        // 4. DspSampleProvider preserves crossfeed/limiter delay buffers across cutoff/balance changes (no 3ms dropout)
        var hotWave = new ContinuousSineProvider(sampleRate: 48000, channels: 2, amplitude: 0.7f);
        var dsp = new DspSampleProvider(hotWave);
        dsp.ConfigureExtendedDsp(false, null, crossfeedEnabled: true, crossfeedCutoffHz: 700.0, crossfeedFeedDb: 4.5, crossfeedDelayMs: 0.28, balanceDb: 0.0, monoFold: false, limiterEnabled: true);

        float[] warmup = new float[512]; // 256 stereo frames > 144-frame limiter lookahead
        Assert.Equal(512, dsp.Read(warmup, 0, warmup.Length));

        // Tweak cutoff, feed dB & balance while preserving crossfeedDelayMs
        dsp.ConfigureExtendedDsp(false, null, crossfeedEnabled: true, crossfeedCutoffHz: 550.0, crossfeedFeedDb: 3.5, crossfeedDelayMs: 0.28, balanceDb: 1.5, monoFold: false, limiterEnabled: true);
        float[] afterUpdate = new float[32]; // next 16 stereo frames must be non-zero immediately
        Assert.Equal(32, dsp.Read(afterUpdate, 0, afterUpdate.Length));
        Assert.All(afterUpdate.Where((_, idx) => idx % 2 == 0).Take(8), s => Assert.True(Math.Abs(s) > 1e-4f));
    }

    private sealed class ContinuousSineProvider(int sampleRate, int channels, float amplitude) : NAudio.Wave.ISampleProvider
    {
        private long _frame;
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            for (int f = 0; f < frames; f++)
            {
                float val = amplitude * (float)Math.Sin(2.0 * Math.PI * 330.0 * (_frame + 1) / sampleRate);
                _frame++;
                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + f * channels + c] = val;
                }
            }
            return frames * channels;
        }
    }
}
