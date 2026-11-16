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
}
