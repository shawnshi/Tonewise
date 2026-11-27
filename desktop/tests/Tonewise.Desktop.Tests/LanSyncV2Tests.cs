using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Services;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class LanSyncV2Tests
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

    private static string CreateEmptyManifestDb(string tempRoot)
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

    private static void WriteMinimalWav(string path, int seed = 440, int seconds = 1, int sampleRate = 44100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int totalSamples = sampleRate * seconds;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = totalSamples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        for (int i = 0; i < totalSamples; i++)
        {
            short sample = (short)(0.35 * Math.Sin(2.0 * Math.PI * seed * i / sampleRate) * 32767.0);
            bw.Write(sample);
        }
    }

    private static async Task<string> PairHttpClientAsync(HttpClient http, SyncHostServer server, string deviceId = "pixel-test")
    {
        string code = server.Store.CurrentPairingCode;
        var resp = await http.PostAsJsonAsync("/api/v2/pair", new
        {
            code,
            device_id = deviceId,
            device_name = "Test Phone"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string token = doc.RootElement.GetProperty("token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    [Fact]
    public void LibraryStore_FastMode_ProducesAuthenticBlake3TrackUid()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s0_fastuid_" + Guid.NewGuid().ToString("N"));
        try
        {
            string wav1 = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            string wav2 = Path.Combine(tempRoot, "Artists", "Tester", "02_Added.wav");
            WriteMinimalWav(wav1, seed: 440);

            string dbPath = CreateEmptyManifestDb(tempRoot);
            var fp1 = TonewiseCoreClient.ComputeFingerprint(wav1);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        $uid, $af, $hsz, $hb, 0,
                        'Artists/Tester/01_Seed.wav', $fsz, $mt, 'wav', 44100, 1000, '01_Seed'
                    );
                    """;
                var fi1 = new FileInfo(wav1);
                ins.Parameters.AddWithValue("$uid", fp1.AudioFrameBlake3Hex);
                ins.Parameters.AddWithValue("$af", Convert.FromHexString(fp1.AudioFrameBlake3Hex));
                ins.Parameters.AddWithValue("$hsz", (long)fp1.HeaderSize);
                ins.Parameters.AddWithValue("$hb", Convert.FromHexString(fp1.HeaderBlake3Hex));
                ins.Parameters.AddWithValue("$fsz", fi1.Length);
                ins.Parameters.AddWithValue("$mt", new DateTimeOffset(fi1.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                ins.ExecuteNonQuery();
            }

            // Add second file and forceRebuild with fastMode: true (exercises BuildManifestWithFfiFallback directly)
            WriteMinimalWav(wav2, seed: 528);
            var expectedFp2 = TonewiseCoreClient.ComputeFingerprint(wav2);

            var store = new LibraryStore();
            bool rebuilt = store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true);
            Assert.True(rebuilt);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT track_uid, lower(hex(audio_frame_blake3)) FROM tracks WHERE rel_path = 'Artists/Tester/02_Added.wav';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(expectedFp2.AudioFrameBlake3Hex, r.GetString(0));
                Assert.Equal(expectedFp2.AudioFrameBlake3Hex, r.GetString(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S0_PlayDelta_AccumulatesIdempotently_ReturnsEffectiveCounts_PreservesLastPlayedTs()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s0_delta_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('a', 64);
            long oldPlayedMs = DateTimeOffset.UtcNow.AddDays(-120).ToUnixTimeMilliseconds();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, legacy_play_count, play_count_total, last_played_ts, rating
                    ) VALUES (
                        $uid, X'AAAA', 16, X'BBBB', 0,
                        'Artist/Album/01.flac', 1200, 1000, 'flac', 44100, 200000,
                        'Old Gem', 10, 10, $oldTs, 5
                    );
                    """;
                ins.Parameters.AddWithValue("$uid", uid);
                ins.Parameters.AddWithValue("$oldTs", oldPlayedMs);
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-delta");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var payload = new
            {
                device_id = "pixel-test",
                play_events = new[]
                {
                    new
                    {
                        event_id = "ev_001",
                        track_uid = uid,
                        play_count_delta = 3,
                        last_played_ts_ms = 0L,
                        device_id = "pixel-test"
                    }
                }
            };

            var resp1 = await http.PostAsJsonAsync("/api/v2/report", payload);
            resp1.EnsureSuccessStatusCode();
            using (var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync()))
            {
                Assert.Equal(1, doc1.RootElement.GetProperty("merged_tracks").GetInt32());
                Assert.Equal(13, doc1.RootElement.GetProperty("effective_counts").GetProperty(uid).GetInt32());
                var loved = doc1.RootElement.GetProperty("loved_uids").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains(uid, loved);
            }

            // Replaying same event_id must NOT double-increment
            var resp2 = await http.PostAsJsonAsync("/api/v2/report", payload);
            resp2.EnsureSuccessStatusCode();
            using (var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync()))
            {
                Assert.Equal(0, doc2.RootElement.GetProperty("merged_tracks").GetInt32());
                Assert.Equal(13, doc2.RootElement.GetProperty("effective_counts").GetProperty(uid).GetInt32());
            }

            // Verify last_played_ts was preserved (not clobbered with UtcNow)
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT play_count_total, last_played_ts FROM tracks WHERE track_uid = $uid;";
                q.Parameters.AddWithValue("$uid", uid);
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(13, r.GetInt32(0));
                Assert.Equal(oldPlayedMs, r.GetInt64(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S0_MutationsPush_ReturnsAcknowledgedKeys_AndUnmatchedUids()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s0_mut_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string knownUid = new string('b', 64);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, rating, rating_ts
                    ) VALUES (
                        $uid, X'BBBB', 16, X'CCCC', 0,
                        'Artist/01.flac', 1000, 1000, 'flac', 44100, 180000, 'Track One', 0, 100
                    );
                    """;
                ins.Parameters.AddWithValue("$uid", knownUid);
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-mut");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var resp = await http.PostAsJsonAsync("/api/v1/mutations/push", new
            {
                device_id = "pixel-test",
                mutations = new object[]
                {
                    new { mutation_key = "k_ok", track_uid = knownUid, field = "rating", value = "5", updated_at_ms = 5000L },
                    new { mutation_key = "k_miss", track_uid = "uid_missing_999", field = "rating", value = "5", updated_at_ms = 5000L }
                }
            });
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetProperty("applied_count").GetInt32());
            var ackKeys = doc.RootElement.GetProperty("acknowledged_keys").EnumerateArray().Select(e => e.GetString()).ToList();
            var unmatched = doc.RootElement.GetProperty("unmatched_uids").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["k_ok"], ackKeys);
            Assert.Equal(["uid_missing_999"], unmatched);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT rating FROM tracks WHERE track_uid = $uid;";
                q.Parameters.AddWithValue("$uid", knownUid);
                Assert.Equal(5L, Convert.ToInt64(q.ExecuteScalar()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S0_PlaylistMerge_UsesPcActualM3u8AsOurs_AndNormalizesPaths()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s0_m3u_" + Guid.NewGuid().ToString("N"));
        try
        {
            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);
            File.WriteAllText(
                Path.Combine(playlistsDir, "Roadtrip.m3u8"),
                "#EXTM3U\n#PLAYLIST:Roadtrip\nArtist/Album/01.flac\nArtist/Album/02.flac\nArtist/Album/03.flac\nArtist/Album/04.flac\n",
                new UTF8Encoding(false));

            await using var server = new SyncHostServer(tempRoot, "pc-m3u", allowPhonePlaylistUpload: true);
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var resp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "Roadtrip",
                @base = new[] { "Artist/Album/01.flac", "Artist/Album/02.flac", "Artist/Album/03.flac" },
                // Deliberately send stale ours == base (old mobile bug); server must read PC's real file (which appended 04.flac)!
                ours = new[] { "Artist/Album/01.flac", "Artist/Album/02.flac", "Artist/Album/03.flac" },
                theirs = new[] { "Music/Tonewise/Artist/Album/01.flac", "Music/Tonewise/Artist/Album/03.flac" }
            });
            resp.EnsureSuccessStatusCode();
            var merged = await resp.Content.ReadFromJsonAsync<M3u8MergeResult>();
            Assert.NotNull(merged);
            Assert.Empty(merged!.Conflicts);
            Assert.Equal(["Artist/Album/01.flac", "Artist/Album/03.flac", "Artist/Album/04.flac"], merged.Merged);

            // Also normalize paths rooted directly under Music/ (without Tonewise/ subfolder)
            var musicResp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "Roadtrip",
                @base = new[] { "Artist/Album/01.flac", "Artist/Album/03.flac", "Artist/Album/04.flac" },
                ours = new[] { "Artist/Album/01.flac", "Artist/Album/03.flac", "Artist/Album/04.flac" },
                theirs = new[] { "Music/Artist/Album/01.flac", "Music/Artist/Album/03.flac" }
            });
            musicResp.EnsureSuccessStatusCode();
            var musicMerged = await musicResp.Content.ReadFromJsonAsync<M3u8MergeResult>();
            Assert.NotNull(musicMerged);
            Assert.Empty(musicMerged!.Conflicts);
            Assert.Equal(["Artist/Album/01.flac", "Artist/Album/03.flac"], musicMerged.Merged);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S1_V2AuthGate_RequiresBearerToken_AndSupportsPairAndUnpair()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s1_auth_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateEmptyManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-auth");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // Ping is public
            var pingResp = await http.GetAsync("/api/v2/ping");
            Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);

            // All protected v2 endpoints return 401 without Bearer token
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v2/outbox")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v2/catalog?kind=playlists")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v2/tracks/abc/file")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v2/tracks/abc/cover")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/v2/report", new { })).StatusCode);

            // Wrong pairing code returns 401
            string realCode = server.Store.CurrentPairingCode;
            string wrongCode = realCode == "000000" ? "999999" : "000000";
            var badPair = await http.PostAsJsonAsync("/api/v2/pair", new { code = wrongCode, device_id = "d1", device_name = "Phone" });
            Assert.Equal(HttpStatusCode.Unauthorized, badPair.StatusCode);

            // Right code returns Bearer token and grants access
            await PairHttpClientAsync(http, server, "d1");
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v2/outbox")).StatusCode);

            // Unpair revokes access
            Assert.True(server.Store.UnpairDevice("d1"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v2/outbox")).StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S1_OutboxResolve_All5Granularities_ModesAndDedup()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s1_outbox_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string f1 = Path.Combine(tempRoot, "Artists", "ArtistA", "Alb1", "01.flac");
            string f2 = Path.Combine(tempRoot, "Artists", "ArtistB", "Alb2", "02.flac");
            string f3 = Path.Combine(tempRoot, "Artists", "ArtistC", "Alb3", "03.flac");
            string f4 = Path.Combine(tempRoot, "Artists", "ArtistD", "Alb4", "04.flac");
            foreach (string f in new[] { f1, f2, f3, f4 })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllBytes(f, [0x66, 0x4C, 0x61, 0x43, 1, 2, 3, 4]);
            }
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(f1)!, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

            Directory.CreateDirectory(Path.Combine(tempRoot, "Playlists"));
            File.WriteAllText(
                Path.Combine(tempRoot, "Playlists", "Favs.m3u8"),
                "#EXTM3U\n#PLAYLIST:Favs\nArtists/ArtistA/Alb1/01.flac\nArtists/ArtistB/Alb2/02.flac\n",
                new UTF8Encoding(false));

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes, rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, album, album_artist, artists_json, genre) VALUES
                    ('u1', X'01', 4, X'11', 0, 'Artists/ArtistA/Alb1/01.flac', 8, 1000, 'flac', 44100, 10000, 'S1', 'Alb1', 'ArtistA', '["ArtistA"]', 'Jazz'),
                    ('u2', X'02', 4, X'12', 0, 'Artists/ArtistB/Alb2/02.flac', 8, 1000, 'flac', 44100, 10000, 'S2', 'Alb2', 'ArtistB', '["ArtistB"]', 'Rock'),
                    ('u3', X'03', 4, X'13', 0, 'Artists/ArtistC/Alb3/03.flac', 8, 1000, 'flac', 44100, 10000, 'S3', 'Alb3', 'ArtistC', '["ArtistC"]', 'Jazz'),
                    ('u4', X'04', 4, X'14', 0, 'Artists/ArtistD/Alb4/04.flac', 8, 1000, 'flac', 44100, 10000, 'S4', 'Alb4', 'ArtistD', '["ArtistD"]', 'Pop');
                    """;
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-outbox");
            // 5 granularities: playlist, genre, artist, single track, multi tracks (u1 overlaps across playlist+genre+multi!)
            var obPl = server.Store.EnqueuePlaylist("Favs");
            var obGenre = server.Store.EnqueueGenre("Jazz");
            var obArtist = server.Store.EnqueueArtist("ArtistB");
            var obSingle = server.Store.EnqueueTracks(["u4"], "S4");
            var obMulti = server.Store.EnqueueTracks(["u1", "u4"], "2 selected");

            Assert.Equal("subscribe", obPl.Mode);
            Assert.Equal("subscribe", obGenre.Mode);
            Assert.Equal("subscribe", obArtist.Mode);
            Assert.Equal("copy", obSingle.Mode);
            Assert.Equal("copy", obMulti.Mode);

            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var respText = await http.GetStringAsync("/api/v2/outbox");
            using var doc = JsonDocument.Parse(respText);
            var tracks = doc.RootElement.GetProperty("tracks").EnumerateArray().ToList();
            Assert.Equal(4, tracks.Count);
            var u1 = tracks.Single(t => t.GetProperty("track_uid").GetString() == "u1");
            Assert.True(u1.GetProperty("has_cover").GetBoolean());
            var u1Owners = u1.GetProperty("source_outbox_ids").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains(obPl.Id, u1Owners);
            Assert.Contains(obGenre.Id, u1Owners);
            Assert.Contains(obMulti.Id, u1Owners);

            var playlists = doc.RootElement.GetProperty("playlists").EnumerateArray().ToList();
            Assert.Single(playlists);
            Assert.Equal("Favs", playlists[0].GetProperty("name").GetString());
            Assert.Equal(2, playlists[0].GetProperty("rel_paths").GetArrayLength());
            Assert.Equal(32L, doc.RootElement.GetProperty("total_bytes").GetInt64());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S1_TrackFileDownload_SupportsHttpRangeResume_AndBlocksPathTraversal_AndServesCover()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s1_range_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
            byte[] coverBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0xFF, 0xD9];

            string albumDir = Path.Combine(tempRoot, "Artists", "Ryuichi", "Async");
            Directory.CreateDirectory(albumDir);
            File.WriteAllBytes(Path.Combine(albumDir, "01.flac"), payload);
            File.WriteAllBytes(Path.Combine(albumDir, "cover.jpg"), coverBytes);

            string outsideFile = Path.Combine(Path.GetDirectoryName(tempRoot)!, "secret_outside_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outsideFile, "secret");

            try
            {
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var ins = conn.CreateCommand();
                    ins.CommandText = """
                        INSERT INTO tracks (track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes, rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title) VALUES
                        ('uid_range', X'AA', 4, X'BB', 0, 'Artists/Ryuichi/Async/01.flac', 512, 1000, 'flac', 96000, 10000, 'Async'),
                        ('uid_evil', X'CC', 4, X'DD', 0, $evilRel, 6, 1000, 'flac', 44100, 10000, 'Evil');
                        """;
                    ins.Parameters.AddWithValue("$evilRel", "../" + Path.GetFileName(outsideFile));
                    ins.ExecuteNonQuery();
                }

                await using var server = new SyncHostServer(tempRoot, "pc-range");
                int port = await server.StartAsync(port: 0, enableMdns: false);
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                await PairHttpClientAsync(http, server);

                // 1. Range chunk 1: 0..199
                using var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/v2/tracks/uid_range/file");
                req1.Headers.Range = new RangeHeaderValue(0, 199);
                using var resp1 = await http.SendAsync(req1);
                Assert.Equal(HttpStatusCode.PartialContent, resp1.StatusCode);
                byte[] part1 = await resp1.Content.ReadAsByteArrayAsync();
                Assert.Equal(200, part1.Length);

                // 2. Range chunk 2: 200..
                using var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/v2/tracks/uid_range/file");
                req2.Headers.Range = new RangeHeaderValue(200, null);
                using var resp2 = await http.SendAsync(req2);
                Assert.Equal(HttpStatusCode.PartialContent, resp2.StatusCode);
                byte[] part2 = await resp2.Content.ReadAsByteArrayAsync();
                Assert.Equal(312, part2.Length);

                Assert.Equal(payload, part1.Concat(part2).ToArray());

                // 3. Cover endpoint
                var coverResp = await http.GetAsync("/api/v2/tracks/uid_range/cover");
                Assert.Equal(HttpStatusCode.OK, coverResp.StatusCode);
                Assert.Equal(coverBytes, await coverResp.Content.ReadAsByteArrayAsync());

                // 4. Path traversal rejected
                var evilResp = await http.GetAsync("/api/v2/tracks/uid_evil/file");
                Assert.Equal(HttpStatusCode.NotFound, evilResp.StatusCode);
            }
            finally
            {
                if (File.Exists(outsideFile)) File.Delete(outsideFile);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S1_Report_RemovesCompletedCopyOutboxItems_KeepsSubscriptions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s1_copyclear_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateEmptyManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-copy");
            var sub = server.Store.EnqueueGenre("Classical", mode: "subscribe");
            var copy = server.Store.EnqueueTracks(["u1"], "Single Track", mode: "copy");

            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            var reportResp = await http.PostAsJsonAsync("/api/v2/report", new
            {
                completed_outbox_ids = new[] { sub.Id, copy.Id }
            });
            reportResp.EnsureSuccessStatusCode();

            var remaining = server.Store.GetOutboxItems();
            Assert.Single(remaining);
            Assert.Equal(sub.Id, remaining[0].Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnqueuePlaylists_BatchDeduplicatesTracks_AndEmitsHeaderAndAudioHashes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s1_batch_pl_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string f1 = Path.Combine(tempRoot, "ArtistA", "Alb1", "01.flac");
            string f2 = Path.Combine(tempRoot, "ArtistB", "Alb2", "02.flac");
            foreach (string f in new[] { f1, f2 })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllBytes(f, [0x66, 0x4C, 0x61, 0x43, 1, 2, 3, 4]);
            }

            Directory.CreateDirectory(Path.Combine(tempRoot, "Playlists"));
            File.WriteAllText(
                Path.Combine(tempRoot, "Playlists", "Mix1.m3u8"),
                "#EXTM3U\n#PLAYLIST:Mix1\nArtistA/Alb1/01.flac\nArtistB/Alb2/02.flac\nArtistA/Alb1/01.flac\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(tempRoot, "Playlists", "Mix2.m3u8"),
                "#EXTM3U\n#PLAYLIST:Mix2\nArtistB/Alb2/02.flac\n",
                new UTF8Encoding(false));

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes, rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, album, album_artist, artists_json, genre) VALUES
                    ('u1', X'AA11', 4, X'BB22', 0, 'ArtistA/Alb1/01.flac', 8, 2000, 'flac', 44100, 10000, 'Song 1', 'Alb1', 'ArtistA', '["ArtistA"]', 'Rock'),
                    ('u2', X'CC33', 4, X'DD44', 0, 'ArtistB/Alb2/02.flac', 8, 3000, 'flac', 44100, 10000, 'Song 2', 'Alb2', 'ArtistB', '["ArtistB"]', 'Pop');
                    """;
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-batch-pl");
            var added = server.Store.EnqueuePlaylists(["Mix1", "Mix2", "mix1"]);
            Assert.Equal(2, added.Count);

            var payload = server.Store.ResolveOutbox();
            Assert.Equal(2, payload.Items.Count);
            Assert.Equal(2, payload.Playlists.Count);
            Assert.Equal(2, payload.Tracks.Count);
            Assert.Equal(2, payload.DuplicateTrackCount);
            Assert.Equal(16L, payload.TotalBytes);

            var u1 = payload.Tracks.Single(t => t.TrackUid == "u1");
            Assert.Equal("bb22", u1.HeaderBlake3);
            Assert.Equal("aa11", u1.AudioFrameBlake3);
            var u2 = payload.Tracks.Single(t => t.TrackUid == "u2");
            Assert.Equal(2, u2.SourceOutboxIds.Count);
            Assert.Equal("dd44", u2.HeaderBlake3);
            Assert.Equal("cc33", u2.AudioFrameBlake3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S0_PlaylistMerge_PreservesSpacesAndDotsInPlaylistName_AndWritesPlaylistDirective()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_s0_pl_names_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-m3u-names", allowPhonePlaylistUpload: true);
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server);

            foreach (string plName in new[] { "All Out 2010s", "Vol. 1 Classics" })
            {
                var resp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
                {
                    playlist_name = plName,
                    @base = Array.Empty<string>(),
                    ours = Array.Empty<string>(),
                    theirs = new[] { "Artist/Album/01.flac" },
                });
                resp.EnsureSuccessStatusCode();
                var merged = await resp.Content.ReadFromJsonAsync<M3u8MergeResult>();
                Assert.NotNull(merged);
                Assert.Empty(merged!.Conflicts);

                string expectedPath = Path.Combine(tempRoot, "Playlists", plName + ".m3u8");
                Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}");
                string content = File.ReadAllText(expectedPath, Encoding.UTF8);
                Assert.Contains($"#PLAYLIST:{plName}", content);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Phase1_V1Endpoints_RequireBearerAuth_AndRejectNonJsonPost()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p1_v1auth_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateEmptyManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-p1-auth");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/manifest")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("/api/v1/playlists/merge", new { playlists = Array.Empty<object>() })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("/api/v1/mutations/push", new { device_id = "d1", mutations = Array.Empty<object>() })).StatusCode);

            await PairHttpClientAsync(http, server, "p1-auth-dev");

            using var textBody = new StringContent("{}", Encoding.UTF8, "text/plain");
            var nonJsonResp = await http.PostAsync("/api/v1/playlists/merge", textBody);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonJsonResp.StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Phase1_PairingRateLimit_LocksOutAfterMaxFailures_AndOversizePatchReturns413()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p1_lockout_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string oversizeAudio = Path.Combine(tempRoot, "Artist", "big.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(oversizeAudio)!);
            File.WriteAllBytes(oversizeAudio, [0x66, 0x4C, 0x61, 0x43]);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'uid_oversize', X'1122', 3145728, X'3344', 0,
                        'Artist/big.flac', 4000000, 1000, 'flac', 44100, 180000, 'Oversize Header'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-p1-lock");
            int port = await server.StartAsync(port: 0, enableMdns: false);

            // Lock out a dedicated pairing server via 5 bad attempts -> 401 x4 then 429 + Retry-After
            using var anonHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            string realCode = server.Store.CurrentPairingCode;
            string wrongCode = realCode == "000000" ? "999999" : "000000";

            for (int i = 1; i <= 4; i++)
            {
                var badResp = await anonHttp.PostAsJsonAsync("/api/v2/pair", new { code = wrongCode, device_id = "brute", device_name = "Brute" });
                Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);
            }

            var lockResp = await anonHttp.PostAsJsonAsync("/api/v2/pair", new { code = wrongCode, device_id = "brute", device_name = "Brute" });
            Assert.Equal(HttpStatusCode.TooManyRequests, lockResp.StatusCode);
            Assert.True(lockResp.Headers.TryGetValues("Retry-After", out _));

            // Even the real code is rejected while locked out
            var realDuringLockResp = await anonHttp.PostAsJsonAsync("/api/v2/pair", new { code = realCode, device_id = "legit", device_name = "Legit" });
            Assert.Equal(HttpStatusCode.TooManyRequests, realDuringLockResp.StatusCode);

            // Use a fresh non-locked host instance to verify paired /api/v1/patch oversize cap (3 MiB > 2 MiB -> 413)
            string tempRoot2 = Path.Combine(Path.GetTempPath(), "tw_p1_patch413_" + Guid.NewGuid().ToString("N"));
            try
            {
                string dbPath2 = CreateEmptyManifestDb(tempRoot2);
                string oversizeAudio2 = Path.Combine(tempRoot2, "Artist", "big.flac");
                Directory.CreateDirectory(Path.GetDirectoryName(oversizeAudio2)!);
                File.WriteAllBytes(oversizeAudio2, [0x66, 0x4C, 0x61, 0x43]);
                using (var conn = new SqliteConnection($"Data Source={dbPath2}"))
                {
                    conn.Open();
                    using var ins = conn.CreateCommand();
                    ins.CommandText = """
                        INSERT INTO tracks (
                            track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                            rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                        ) VALUES (
                            'uid_oversize', X'1122', 3145728, X'3344', 0,
                            'Artist/big.flac', 4000000, 1000, 'flac', 44100, 180000, 'Oversize Header'
                        );
                        """;
                    ins.ExecuteNonQuery();
                }

                await using var server2 = new SyncHostServer(tempRoot2, "pc-p1-patch");
                int port2 = await server2.StartAsync(port: 0, enableMdns: false);
                using var authedHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port2}") };
                await PairHttpClientAsync(authedHttp, server2, "p1-patch-dev");

                var oversizeResp = await authedHttp.GetAsync("/api/v1/patch/uid_oversize");
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizeResp.StatusCode);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempRoot2)) Directory.Delete(tempRoot2, recursive: true);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Phase2_ManifestEndpoint_ServesVacuumIntoConsistentSnapshot_UnderConcurrentWalWrites()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p2_snap_" + Guid.NewGuid().ToString("N"));
        string snapDownload = Path.Combine(Path.GetTempPath(), "tw_p2_dl_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-p2-snap");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(http, server, "p2-snap-dev");

            // Keep an open WAL read-write connection and insert a row via immediate tx without checkpointing main DB
            using (var writerConn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var walMode = writerConn.CreateCommand())
                {
                    walMode.CommandText = "PRAGMA wal_autocheckpoint = 0;";
                    walMode.ExecuteNonQuery();
                }

                using var tx = TonewiseSqlite.BeginImmediateTransaction(writerConn);
                using var ins = writerConn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'uid_wal_1', X'5566', 16, X'7788', 0,
                        'Artist/wal_song.flac', 2048, 1000, 'flac', 44100, 120000, 'WAL Snapshot Track'
                    );
                    """;
                ins.ExecuteNonQuery();
                tx.Commit();

                byte[] downloadedDb = await http.GetByteArrayAsync("/api/v1/manifest");
                File.WriteAllBytes(snapDownload, downloadedDb);
            }

            using var readConn = new SqliteConnection($"Data Source={snapDownload};Mode=ReadOnly;Pooling=False");
            readConn.Open();
            using (var integrityCmd = readConn.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", integrityCmd.ExecuteScalar()?.ToString());
            }
            using (var q = readConn.CreateCommand())
            {
                q.CommandText = "SELECT title FROM tracks WHERE track_uid = 'uid_wal_1';";
                Assert.Equal("WAL Snapshot Track", q.ExecuteScalar()?.ToString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(snapDownload)) File.Delete(snapDownload);
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Phase2_MultiDeviceWildcardCopyOutbox_RemainsForSecondDeviceUntilAllAck()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p2_multi_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string wavPath = Path.Combine(tempRoot, "Artists", "ArtistS", "01.wav");
            WriteMinimalWav(wavPath);
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, artists_json
                    ) VALUES (
                        'u_shared', X'AA11', 44, X'BB22', 0,
                        'Artists/ArtistS/01.wav', 88244, 1000, 'wav', 44100, 1000, 'Shared Song', '["ArtistS"]'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            await using var server = new SyncHostServer(tempRoot, "pc-p2-multi");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var httpA = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var httpB = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(httpA, server, "dev-a");
            await PairHttpClientAsync(httpB, server, "dev-b");

            var ob = server.Store.EnqueueTracks(["u_shared"], "Shared Song", deviceId: "*", mode: "copy");

            using (var obA1 = JsonDocument.Parse(await httpA.GetStringAsync("/api/v2/outbox")))
                Assert.Single(obA1.RootElement.GetProperty("items").EnumerateArray());
            using (var obB1 = JsonDocument.Parse(await httpB.GetStringAsync("/api/v2/outbox")))
                Assert.Single(obB1.RootElement.GetProperty("items").EnumerateArray());

            var ackA = await httpA.PostAsJsonAsync("/api/v2/report", new
            {
                device_id = "dev-a",
                play_events = Array.Empty<object>(),
                mutations = Array.Empty<object>(),
                completed_outbox_ids = new[] { ob.Id }
            });
            ackA.EnsureSuccessStatusCode();

            using (var obA2 = JsonDocument.Parse(await httpA.GetStringAsync("/api/v2/outbox")))
                Assert.Empty(obA2.RootElement.GetProperty("items").EnumerateArray());
            using (var obB2 = JsonDocument.Parse(await httpB.GetStringAsync("/api/v2/outbox")))
                Assert.Single(obB2.RootElement.GetProperty("items").EnumerateArray());

            var ackB = await httpB.PostAsJsonAsync("/api/v2/report", new
            {
                device_id = "dev-b",
                play_events = Array.Empty<object>(),
                mutations = Array.Empty<object>(),
                completed_outbox_ids = new[] { ob.Id }
            });
            ackB.EnsureSuccessStatusCode();

            Assert.Empty(server.Store.GetOutboxItems());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncHostServer_LastFmSessionEndpoint_RequiresAuthAndReturnsSavedSession()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_lfm_session_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateEmptyManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-lfm");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var anonHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var anonResp = await anonHttp.GetAsync("/api/v1/lastfm/session");
            Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

            using var authedHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await PairHttpClientAsync(authedHttp, server, "dev-lfm");

            using (var docUnconfigured = JsonDocument.Parse(await authedHttp.GetStringAsync("/api/v1/lastfm/session")))
            {
                Assert.False(docUnconfigured.RootElement.GetProperty("configured").GetBoolean());
            }

            string dotDir = Path.Combine(tempRoot, ".tonewise");
            Tonewise.SyncHost.Ledger.LastFmAccountConfig.Save(
                dotDir,
                new Tonewise.SyncHost.Ledger.LastFmAccountConfig(
                    ApiKey: "0123456789abcdef0123456789abcdef",
                    ApiSecret: "fedcba9876543210fedcba9876543210",
                    Username: "shawn_test",
                    SessionKey: "sk_test_12345"));

            using var docConfigured = JsonDocument.Parse(await authedHttp.GetStringAsync("/api/v1/lastfm/session"));
            Assert.True(docConfigured.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal("shawn_test", docConfigured.RootElement.GetProperty("username").GetString());
            Assert.Equal("0123456789abcdef0123456789abcdef", docConfigured.RootElement.GetProperty("api_key").GetString());
            Assert.Equal("fedcba9876543210fedcba9876543210", docConfigured.RootElement.GetProperty("shared_secret").GetString());
            Assert.Equal("sk_test_12345", docConfigured.RootElement.GetProperty("session_key").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
