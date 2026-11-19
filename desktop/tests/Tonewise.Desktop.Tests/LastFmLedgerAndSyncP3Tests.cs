using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Export;
using Tonewise.SyncHost.Ledger;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class LastFmLedgerAndSyncP3Tests
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

    [Fact]
    public void LastFmLedger_AsymmetricHighWaterMarkFormula_MatchesExactSpec()
    {
        Assert.Equal(23, LastFmLedgerService.ComputeEffectivePlayCount(legacyCount: 20, lastFmPulledCount: 15, localUnsyncedExpiredCount: 3));
        Assert.Equal(21, LastFmLedgerService.ComputeEffectivePlayCount(legacyCount: 8, lastFmPulledCount: 19, localUnsyncedExpiredCount: 2));
        Assert.Equal(5, LastFmLedgerService.ComputeEffectivePlayCount(legacyCount: -1, lastFmPulledCount: 5, localUnsyncedExpiredCount: 0));
    }

    [Fact]
    public void LastFmLedger_14DayWindow_IdempotentCloudDedup_AndLovedSync_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p3_lfm_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        string dbPath = Path.Combine(dotTonewise, "manifest.db");

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
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
                    ) VALUES (
                        'uid_goldberg', X'AA', 64, X'BB', 1024,
                        'Bach/Goldberg/01_Aria.flac', 2048, 1000, 'flac', 96000, 185000,
                        'Aria (Goldberg Variations)', 'BWV 988', '["Glenn Gould"]', 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            new StagingDiffStore(dbPath).EnsureSchema();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE tracks SET legacy_play_count = 10 WHERE track_uid = 'uid_goldberg';";
                upd.ExecuteNonQuery();
            }

            long nowSec = 1_763_251_200L;
            var aliasMap = new Dictionary<string, string> { ["Gould, Glenn"] = "Glenn Gould" };

            // 1. Pull cloud plays with alias correction + loved status
            int pulled = LastFmLedgerService.IngestPulledCloudPlays(
                dbPath,
                dotTonewise,
                [
                    new CloudRecentTrack("Gould, Glenn", "Aria (Goldberg Variations)!", "BWV 988", nowSec - 3600),
                    new CloudRecentTrack("Glenn Gould", "Aria (Goldberg Variations)", "BWV 988", nowSec - 7200),
                ],
                aliasMap
            );
            Assert.Equal(2, pulled);

            int lovedUpdated = LastFmLedgerService.SyncLovedTracks(
                dbPath,
                [new CloudLovedTrack("Glenn Gould", "Aria (Goldberg Variations)", nowSec - 9000)],
                aliasMap
            );
            Assert.Equal(1, lovedUpdated);

            // 2. Write local Android history events:
            //    a) 20 days ago -> expired (> 14 days), adds +1 to local_unsynced_expired_count
            //    b) 1 hour ago - 50s offset -> within +-120s of pulled cloud scrobble -> deduplicated
            //    c) 10 minutes ago -> fresh unscrobbled event -> queued in push batch
            var localLines = new[]
            {
                JsonSerializer.Serialize(new { track_uid = "uid_goldberg", utc_ts_ms = (nowSec - 20L * 86400) * 1000L, device_id = "xiaomi-15" }),
                JsonSerializer.Serialize(new { track_uid = "uid_goldberg", utc_ts_ms = (nowSec - 3650L) * 1000L, device_id = "xiaomi-15" }),
                JsonSerializer.Serialize(new { track_uid = "uid_goldberg", utc_ts_ms = (nowSec - 600L) * 1000L, device_id = "xiaomi-15" }),
            };
            File.WriteAllLines(Path.Combine(dotTonewise, "history.xiaomi.jsonl"), localLines, new UTF8Encoding(false));

            var prep = LastFmLedgerService.PrepareScrobbleBatches(
                dbPath,
                dotTonewise,
                nowSec,
                aliasMap,
                [new CloudRecentTrack("Glenn Gould", "Aria (Goldberg Variations)", "BWV 988", nowSec - 3600)]
            );
            Assert.Equal(1, prep.ExpiredCount);
            Assert.Equal(1, prep.DeduplicatedCount);
            Assert.Single(prep.Batches);
            Assert.Single(prep.Batches[0].Items);
            Assert.Equal(nowSec - 600L, prep.Batches[0].Items[0].PlayedAtUnixSec);

            // Verify SQLite ledger: max(legacy=10, pulled=2) + expired=1 = 11, rating = 5
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT legacy_play_count, lastfm_pulled_count, local_unsynced_expired_count, play_count_total, rating FROM tracks WHERE track_uid = 'uid_goldberg';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(10, r.GetInt32(0));
                Assert.Equal(2, r.GetInt32(1));
                Assert.Equal(1, r.GetInt32(2));
                Assert.Equal(11, r.GetInt32(3));
                Assert.Equal(5, r.GetInt32(4));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncHost_Encyclopedia_HeaderPatch_And_UsbCarExporter_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p3_sync_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        string albumDir = Path.Combine(tempRoot, "Artists", "Ryuichi Sakamoto");
        string usbDir = Path.Combine(tempRoot, "usb_drive");
        Directory.CreateDirectory(dotTonewise);
        Directory.CreateDirectory(albumDir);

        try
        {
            string audioRel = "Artists/Ryuichi Sakamoto/01_Async.flac";
            string audioAbs = Path.Combine(tempRoot, audioRel.Replace('/', Path.DirectorySeparatorChar));
            byte[] fakeHeader = [0x66, 0x4C, 0x61, 0x43, 0xDE, 0xAD, 0xBE, 0xEF];
            byte[] fakeAudio = [0x66, 0x4C, 0x61, 0x43, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44];
            File.WriteAllBytes(audioAbs, fakeAudio);
            File.WriteAllBytes(Path.Combine(albumDir, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

            string dbPath = Path.Combine(dotTonewise, "manifest.db");
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'uid_async', X'1122', 8, X'3344', 512,
                        'Artists/Ryuichi Sakamoto/01_Async.flac', 12, 1000, 'flac', 96000, 245000, 'Async'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var enc = new EncyclopediaService(Path.Combine(dotTonewise, "encyclopedia.db"));
            enc.UpsertBiography(new EntityBiographyEntry(
                EntityType: EncyclopediaEntityType.Composer,
                NormalizedName: "ryuichi sakamoto",
                DisplayName: "Ryuichi Sakamoto",
                Mbid: "mbid-sakamoto",
                WikidataQid: "Q180726",
                BirthDeathYears: "1952–2023",
                Era: "Contemporary",
                SummaryZh: "日本作曲家与电子音乐先驱。",
                SummaryEn: null,
                SourceUrl: null,
                UpdatedAtMs: 1_763_251_200_000
            ));

            await using var server = new SyncHostServer(tempRoot, "pc-p3");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "pc-p3-phone",
                device_name = "Test"
            });
            pairResp.EnsureSuccessStatusCode();
            using (var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync()))
            {
                string token = pairDoc.RootElement.GetProperty("token").GetString()!;
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            // 1. /api/v1/encyclopedia returns persisted entries
            using var encDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/encyclopedia?entityType=composer"));
            Assert.Single(encDoc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Q180726", encDoc.RootElement.GetProperty("items")[0].GetProperty("wikidata_qid").GetString());

            // 2. /api/v1/patch/{trackUid} serves exact Header-Only micro-patch bytes
            using var patchDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/patch/uid_async"));
            Assert.Equal(8, patchDoc.RootElement.GetProperty("header_size").GetInt32());
            byte[] decodedHdr = Convert.FromBase64String(patchDoc.RootElement.GetProperty("header_base64").GetString()!);
            Assert.Equal(fakeHeader, decodedHdr);

            // 3. /api/v1/history/push updates last_played_ts in manifest.db
            var pushResp = await http.PostAsJsonAsync("/api/v1/history/push", new
            {
                device_id = "android_p3",
                events = new[] { new { track_uid = "uid_async", utc_ts_ms = 1_763_259_999_000L } }
            });
            pushResp.EnsureSuccessStatusCode();
            using var pushJson = JsonDocument.Parse(await pushResp.Content.ReadAsStringAsync());
            Assert.Equal(1, pushJson.RootElement.GetProperty("merged_tracks").GetInt32());

            // 4. UsbCarExporter generates BOM-free UTF-8 .m3u8 with relative POSIX paths + cover.jpg
            var export = UsbCarExporter.ExportPlaylistToUsb(
                "Night Drive",
                [new UsbExportTrack("uid_async", audioAbs, "Async", "Ryuichi Sakamoto", "async", 245_000)],
                usbDir
            );
            Assert.Equal(1, export.ExportedTracks);
            Assert.Equal(1, export.ExportedCovers);
            byte[] rawM3u = File.ReadAllBytes(export.PlaylistM3u8Path);
            Assert.False(rawM3u.Length >= 3 && rawM3u[0] == 0xEF && rawM3u[1] == 0xBB && rawM3u[2] == 0xBF);
            string m3uText = Encoding.UTF8.GetString(rawM3u);
            Assert.Contains("../Tracks/async/01_Async.flac", m3uText);
            Assert.True(File.Exists(Path.Combine(usbDir, "Tracks", "async", "cover.jpg")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LastFmWebApiClient_PullsTopTrackPlayCounts_AndLovedTracks_WithoutSessionKey()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p4_lfm_top_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        string dbPath = Path.Combine(dotTonewise, "manifest.db");

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();
            }

            new StagingDiffStore(dbPath).EnsureSchema();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, album_artist, artists_json, legacy_play_count, lastfm_pulled_count,
                        local_unsynced_expired_count, play_count_total, rating
                    ) VALUES (
                        'uid_goldberg_top', X'01', 16, X'02', 128,
                        'Bach/01.flac', 100, 1000, 'flac', 96000, 210000,
                        'Aria', 'Goldberg Variations', 'Glenn Gould', '["Johann Sebastian Bach"]',
                        0, 0, 0, 0, 0
                    ), (
                        'uid_unique_title', X'03', 16, X'04', 128,
                        'Ambient/02.flac', 100, 1000, 'flac', 96000, 180000,
                        'Dawn Reflected', 'Morning Sketches', NULL, '["Unknown Ensemble"]',
                        2, 0, 0, 2, 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var handler = new StubHttpHandler(req =>
            {
                string query = req.RequestUri?.Query ?? "";
                if (query.Contains("method=user.gettoptracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """
                        {
                          "toptracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "Aria", "playcount": "18", "artist": { "name": "Glenn Gould" } },
                              { "name": "Dawn Reflected", "playcount": "9", "artist": { "name": "Different Cloud ArtistTag" } }
                            ]
                          }
                        }
                        """;
                }
                if (query.Contains("method=user.getrecenttracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """{ "recenttracks": { "@attr": { "totalPages": "1" }, "track": [] } }""";
                }
                if (query.Contains("method=user.getlovedtracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """
                        {
                          "lovedtracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "Aria", "artist": { "name": "Glenn Gould" } }
                            ]
                          }
                        }
                        """;
                }
                return "{}";
            });

            using var httpClient = new HttpClient(handler);
            var client = new LastFmWebApiClient(httpClient);
            var config = new LastFmAccountConfig(
                ApiKey: "test-key",
                ApiSecret: "",
                Username: "testuser",
                SessionKey: "",
                LastPullUnixSec: 0);

            var summary = await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, config);
            Assert.Equal(2, summary.TopPlayCountsUpdated);
            Assert.Equal(1, summary.LovedTracksUpdated);
            Assert.Equal(0, summary.PushedScrobbles);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q1 = conn.CreateCommand();
                q1.CommandText = "SELECT lastfm_pulled_count, play_count_total, rating FROM tracks WHERE track_uid = 'uid_goldberg_top';";
                using var r1 = q1.ExecuteReader();
                Assert.True(r1.Read());
                Assert.Equal(18, r1.GetInt32(0));
                Assert.Equal(18, r1.GetInt32(1));
                Assert.Equal(5, r1.GetInt32(2));

                using var q2 = conn.CreateCommand();
                q2.CommandText = "SELECT lastfm_pulled_count, play_count_total FROM tracks WHERE track_uid = 'uid_unique_title';";
                using var r2 = q2.ExecuteReader();
                Assert.True(r2.Read());
                Assert.Equal(9, r2.GetInt32(0));
                Assert.Equal(9, r2.GetInt32(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BasicMetadataLookupService_FillsOnlyMissingFields_WithoutOverwritingExisting()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p4_meta_" + Guid.NewGuid().ToString("N"));
        string albumDir = Path.Combine(tempRoot, "Artists", "Ryuichi Sakamoto");
        Directory.CreateDirectory(albumDir);

        try
        {
            // Valid minimal JPEG with SOF0 frame encoding 800x800 (0x0320 x 0x0320)
            byte[] validJpeg =
            [
                0xFF, 0xD8,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x03, 0x20, 0x03, 0x20, 0x03, 0x01, 0x11, 0x00,
                0xFF, 0xD9
            ];

            var handler = new StubBinaryHttpHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("itunes.apple.com", StringComparison.OrdinalIgnoreCase))
                {
                    string json = """
                        {
                          "results": [{
                            "trackName": "Solari",
                            "artistName": "Cloud Override Artist",
                            "collectionName": "1996",
                            "primaryGenreName": "Cloud Override Genre",
                            "releaseDate": "1996-05-17T07:00:00Z",
                            "trackTimeMillis": 250000,
                            "artworkUrl100": "https://cdn.example.invalid/cover/100x100bb.jpg"
                          }]
                        }
                        """;
                    return (Encoding.UTF8.GetBytes(json), "application/json");
                }
                if (url.Contains("musicbrainz.org", StringComparison.OrdinalIgnoreCase))
                {
                    string json = """
                        {
                          "recordings": [{
                            "length": 250000,
                            "releases": [{
                              "title": "1996",
                              "date": "1996-05-17",
                              "label-info": [{ "label": { "name": "Milan Records" } }]
                            }]
                          }]
                        }
                        """;
                    return (Encoding.UTF8.GetBytes(json), "application/json");
                }
                if (url.Contains("cdn.example.invalid", StringComparison.OrdinalIgnoreCase))
                {
                    return (validJpeg, "image/jpeg");
                }
                return (null, "text/plain");
            });

            using var httpClient = new HttpClient(handler);
            var service = new BasicMetadataLookupService(httpClient);
            var query = new BasicMetadataQuery(
                TrackUid: "uid_solari",
                FullPath: Path.Combine(albumDir, "01 - Solari.flac"),
                RelPath: "Artists/Ryuichi Sakamoto/01 - Solari.flac",
                Title: "Solari",
                Artists: "Original Artist",
                Album: "",
                Composers: "",
                Conductors: "",
                WorkName: "",
                MovementName: "",
                Genre: "Rock",
                Year: null,
                Publisher: "",
                DurationMs: 250000,
                AlbumDirectory: albumDir,
                CoverArtPath: null);

            var (updates, savedCover) = await service.ResolveMissingMetadataAsync(query);

            // Must NOT overwrite non-empty Artists or Genre
            Assert.False(updates.ContainsKey("artists_json"));
            Assert.False(updates.ContainsKey("genre"));

            // Must fill missing Album, Year, Publisher, and save cover.jpg
            Assert.Equal("1996", updates["album"]);
            Assert.Equal("1996", updates["year"]);
            Assert.Equal("Milan Records", updates["publisher"]);
            Assert.True(savedCover);
            Assert.True(File.Exists(Path.Combine(albumDir, "cover.jpg")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LastFmWebApiClient_MatchesTraditionalSimplifiedChinese_EditionSuffixes_MultiCopy_AndSkewedClockFallback()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p4_lfm_cjk_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        string dbPath = Path.Combine(dotTonewise, "manifest.db");

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();
            }

            new StagingDiffStore(dbPath).EnsureSchema();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, artists_json, legacy_play_count, lastfm_pulled_count,
                        local_unsynced_expired_count, play_count_total, rating
                    ) VALUES (
                        'uid_chen_trad', X'11', 16, X'12', 128,
                        'Pop/01.flac', 100, 1000, 'flac', 44100, 220000,
                        '滾滾紅塵', '精選', '["陳淑樺"]', 0, 0, 0, 0, 0
                    ), (
                        'uid_hey_jude_1', X'21', 16, X'22', 128,
                        'Beatles/A/01.flac', 100, 1000, 'flac', 96000, 420000,
                        'Hey Jude', '1', '["The Beatles"]', 0, 0, 0, 0, 0
                    ), (
                        'uid_hey_jude_2', X'31', 16, X'32', 128,
                        'Beatles/Past/01.flac', 100, 1000, 'flac', 96000, 420000,
                        'Hey Jude', 'Past Masters', '["The Beatles"]', 0, 0, 0, 0, 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var handler = new StubHttpHandler(req =>
            {
                string q = req.RequestUri?.Query ?? "";
                if (q.Contains("method=user.gettoptracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """
                        {
                          "toptracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "滚滚红尘", "playcount": "42", "artist": { "name": "陈淑桦" } },
                              { "name": "Hey Jude - Remastered 2015", "playcount": "31", "artist": { "name": "The Beatles" } }
                            ]
                          }
                        }
                        """;
                }
                if (q.Contains("method=user.getrecenttracks", StringComparison.OrdinalIgnoreCase))
                {
                    // Simulate future/skewed &from= watermark returning empty until fallback fromUnixSec=0 is queried
                    if (q.Contains("&from=", StringComparison.OrdinalIgnoreCase))
                        return """{ "recenttracks": { "@attr": { "totalPages": "1" }, "track": [] } }""";
                    return """
                        {
                          "recenttracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "滚滚红尘", "artist": { "#text": "陈淑桦" }, "album": { "#text": "" }, "date": { "uts": "1730000500" } }
                            ]
                          }
                        }
                        """;
                }
                if (q.Contains("method=user.getlovedtracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """
                        {
                          "lovedtracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "滚滚红尘", "artist": { "name": "陈淑桦" } },
                              { "name": "Hey Jude - Remastered 2015", "artist": { "name": "The Beatles" } }
                            ]
                          }
                        }
                        """;
                }
                return "{}";
            });

            using var httpClient = new HttpClient(handler);
            var client = new LastFmWebApiClient(httpClient);
            var config = new LastFmAccountConfig("k", "", "u", "", LastPullUnixSec: 1_900_000_000L);
            var summary = await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, config);

            Assert.Equal(3, summary.TopPlayCountsUpdated);
            Assert.Equal(3, summary.LovedTracksUpdated);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT track_uid, play_count_total, rating FROM tracks ORDER BY track_uid;";
                using var r = cmd.ExecuteReader();
                int rows = 0;
                while (r.Read())
                {
                    rows++;
                    Assert.True(r.GetInt32(1) >= 31);
                    Assert.Equal(5, r.GetInt32(2));
                }
                Assert.Equal(3, rows);
            }

            var updatedCfg = LastFmAccountConfig.Load(dotTonewise);
            Assert.Equal(1730000501L, updatedCfg!.LastPullUnixSec);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Phase1_LastFmAccountConfig_EncryptsSecretsWithDpapiOnWindows()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p1_dpapi_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            const string plainSecret = "secret_top_value_987654";
            const string plainSession = "session_key_secret_123456";
            var original = new LastFmAccountConfig(
                ApiKey: "public_api_key_abc",
                ApiSecret: plainSecret,
                Username: "shawn",
                SessionKey: plainSession,
                LastPullUnixSec: 1_763_251_200L);

            LastFmAccountConfig.Save(tempDir, original);

            string jsonPath = Path.Combine(tempDir, "lastfm_config.json");
            Assert.True(File.Exists(jsonPath));
            string rawJson = File.ReadAllText(jsonPath, Encoding.UTF8);

            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("dpapi:", rawJson, StringComparison.Ordinal);
                Assert.DoesNotContain(plainSecret, rawJson, StringComparison.Ordinal);
                Assert.DoesNotContain(plainSession, rawJson, StringComparison.Ordinal);
            }

            var loaded = LastFmAccountConfig.Load(tempDir);
            Assert.NotNull(loaded);
            Assert.Equal("public_api_key_abc", loaded!.ApiKey);
            Assert.Equal(plainSecret, loaded.ApiSecret);
            Assert.Equal("shawn", loaded.Username);
            Assert.Equal(plainSession, loaded.SessionKey);
            Assert.Equal(1_763_251_200L, loaded.LastPullUnixSec);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Phase2_LastFmLedger_LocalPlayCountIsNotClobberedBySubsequentCloudSync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p2_lfm_mono_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        string dbPath = Path.Combine(dotTonewise, "manifest.db");

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, artists_json, play_count_total
                    ) VALUES (
                        'uid_mono', X'CCDD', 64, X'EEFF', 0,
                        'Bach/01_Aria.flac', 2048, 1000, 'flac', 96000, 185000,
                        'Aria', 'Goldberg', '["Glenn Gould"]', 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            new StagingDiffStore(dbPath).EnsureSchema();
            long nowSec = 1_763_251_200L;
            for (int i = 0; i < 5; i++)
            {
                LastFmLedgerService.RecordLocalPlaybackEvent(
                    dbPath,
                    dotTonewise,
                    "uid_mono",
                    "Glenn Gould",
                    "Aria",
                    "Goldberg",
                    nowSec - 60L + i,
                    scrobbleEligible: true,
                    deviceId: "desktop");
            }

            LastFmLedgerService.IngestPulledCloudPlays(
                dbPath,
                dotTonewise,
                [new CloudRecentTrack("Glenn Gould", "Aria", "Goldberg", nowSec - 300L)]);
            LastFmLedgerService.SyncTopTrackPlayCounts(
                dbPath,
                [new CloudTopTrack("Glenn Gould", "Aria", 2)]);

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            {
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT play_count_total FROM tracks WHERE track_uid = 'uid_mono';";
                Assert.Equal(5, Convert.ToInt32(q.ExecuteScalar()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Phase2_LastFmWebApiClient_RetriesTransient429AndThrowsOnPersistentFailure()
    {
        int transientAttempts = 0;
        var retryHandler = new SequenceStatusHandler(_ =>
        {
            transientAttempts++;
            if (transientAttempts == 1)
            {
                return (HttpStatusCode.TooManyRequests, """{"error":29,"message":"Rate limit"}""");
            }
            string okJson = """
                {
                  "recenttracks": {
                    "@attr": { "totalPages": "1" },
                    "track": [
                      { "name": "Aria", "artist": { "#text": "Glenn Gould" }, "album": { "#text": "Goldberg" }, "date": { "uts": "1763251200" } }
                    ]
                  }
                }
                """;
            return (HttpStatusCode.OK, okJson);
        });
        var retryClient = new LastFmWebApiClient(new HttpClient(retryHandler));
        var pulled = await retryClient.FetchRecentTracksAsync("api_key", "shawn");
        Assert.Equal(2, transientAttempts);
        Assert.Single(pulled);
        Assert.Equal("Aria", pulled[0].Title);

        int failAttempts = 0;
        var failHandler = new SequenceStatusHandler(_ =>
        {
            failAttempts++;
            return (HttpStatusCode.InternalServerError, """{"error":8,"message":"Backend down"}""");
        });
        var failClient = new LastFmWebApiClient(new HttpClient(failHandler));
        await Assert.ThrowsAsync<HttpRequestException>(() => failClient.FetchRecentTracksAsync("api_key", "shawn"));
        Assert.Equal(3, failAttempts);
    }

    private sealed class SequenceStatusHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, json) = responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json = responder(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubBinaryHttpHandler(Func<HttpRequestMessage, (byte[]? Body, string MediaType)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (body, mediaType) = responder(request);
            if (body is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return Task.FromResult(msg);
        }
    }
}
