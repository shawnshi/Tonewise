using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Services;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class Batch1CrossPlatformFoundationTests
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

    private static byte[] BuildSyntheticFlac(byte[] comment, int paddingLen, byte[] audio)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("fLaC"));
        // STREAMINFO (34 bytes), not last
        ms.WriteByte(0x00);
        ms.Write([0, 0, 34]);
        ms.Write(new byte[34]);
        // VORBIS_COMMENT (type 4), not last
        ms.WriteByte(0x04);
        ms.Write([(byte)((comment.Length >> 16) & 0xFF), (byte)((comment.Length >> 8) & 0xFF), (byte)(comment.Length & 0xFF)]);
        ms.Write(comment);
        // PADDING (type 1), last
        ms.WriteByte(0x81);
        ms.Write([(byte)((paddingLen >> 16) & 0xFF), (byte)((paddingLen >> 8) & 0xFF), (byte)(paddingLen & 0xFF)]);
        ms.Write(new byte[paddingLen]);
        ms.Write(audio);
        return ms.ToArray();
    }

    [Fact]
    public void LibraryStore_IncrementalRebuild_UpdatesHeaderHashOnEdit_AndScansDsfAiff()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b1_lib_" + Guid.NewGuid().ToString("N"));
        string albumDir = Path.Combine(tempRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);

        try
        {
            string flacPath = Path.Combine(albumDir, "01_Track.flac");
            byte[] audio = Encoding.UTF8.GetBytes("BATCH1_FLAC_PURE_AUDIO_FRAMES_ABCDEF");
            File.WriteAllBytes(flacPath, BuildSyntheticFlac(Encoding.UTF8.GetBytes("TITLE=Original"), 64, audio));

            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: false, fastMode: true));

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            string initialUid;
            byte[] initialHeaderHash;
            long initialHeaderSize;
            long initialFileSize;

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT track_uid, header_blake3, header_size, file_size FROM tracks WHERE rel_path = 'Artist/Album/01_Track.flac';";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                initialUid = r.GetString(0);
                initialHeaderHash = (byte[])r["header_blake3"];
                initialHeaderSize = r.GetInt64(2);
                initialFileSize = r.GetInt64(3);
            }

            // Modify FLAC metadata block + file size + mtime while keeping audio frames identical, and add .dsf & .aiff
            File.WriteAllBytes(flacPath, BuildSyntheticFlac(Encoding.UTF8.GetBytes("TITLE=UpdatedTitle\\0COMMENT=Edited"), 96, audio));
            File.SetLastWriteTimeUtc(flacPath, DateTime.UtcNow.AddSeconds(10));
            File.WriteAllBytes(Path.Combine(albumDir, "02_Dsd.dsf"), Encoding.ASCII.GetBytes("DSD \x1c\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0PAYLOAD_DSD"));
            File.WriteAllBytes(Path.Combine(albumDir, "03_Aiff.aiff"), Encoding.ASCII.GetBytes("FORM\0\0\0\x20AIFFCOMM\0\0\0\0SSND\0\0\0\0PAYLOAD_AIFF"));

            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT track_uid, header_blake3, header_size, file_size FROM tracks WHERE rel_path = 'Artist/Album/01_Track.flac';";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                string updatedUid = r.GetString(0);
                byte[] updatedHeaderHash = (byte[])r["header_blake3"];
                long updatedHeaderSize = r.GetInt64(2);
                long updatedFileSize = r.GetInt64(3);

                Assert.Equal(initialUid, updatedUid);
                Assert.NotEqual(initialHeaderHash, updatedHeaderHash);
                Assert.True(updatedHeaderSize > initialHeaderSize);
                Assert.True(updatedFileSize > initialFileSize);

                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = "SELECT rel_path FROM tracks ORDER BY rel_path;";
                var rels = new List<string>();
                using var rr = countCmd.ExecuteReader();
                while (rr.Read()) rels.Add(rr.GetString(0));
                Assert.Contains("Artist/Album/02_Dsd.dsf", rels);
                Assert.Contains("Artist/Album/03_Aiff.aiff", rels);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LastFmLedger_LoopB_ScrobbledDeltaVsOfflineDelta_AndWatermarkIdempotency()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b1_loopb_" + Guid.NewGuid().ToString("N"));
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
                        title, album, artists_json
                    ) VALUES
                    ('uid_scrobbled', X'0102', 64, X'0304', 256, 'A/01.flac', 1000, 1000, 'flac', 44100, 200000, 'Blue in Green', 'Kind of Blue', '["Miles Davis"]'),
                    ('uid_offline', X'0506', 64, X'0708', 256, 'A/02.flac', 1000, 1000, 'flac', 44100, 200000, 'So What', 'Kind of Blue', '["Miles Davis"]');
                    """;
                ins.ExecuteNonQuery();
            }

            new StagingDiffStore(dbPath).EnsureSchema();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE tracks
                    SET legacy_play_count = 10,
                        lastfm_pulled_count = 10,
                        local_unsynced_expired_count = 0,
                        play_count_total = 10;
                    """;
                upd.ExecuteNonQuery();
            }

            long nowSec = 1_763_300_000L;
            var payload = JsonDocument.Parse($$"""
                [
                  {
                    "event_id": "dev1:uid_scrobbled:11:{{nowSec * 1000}}",
                    "track_uid": "uid_scrobbled",
                    "play_count_delta": 1,
                    "scrobbled_delta": 1,
                    "device_play_count_total": 11,
                    "last_played_ts_ms": {{nowSec * 1000}},
                    "device_id": "pixel9"
                  },
                  {
                    "event_id": "dev1:uid_offline:11:{{nowSec * 1000}}",
                    "track_uid": "uid_offline",
                    "play_count_delta": 1,
                    "scrobbled_delta": 0,
                    "device_play_count_total": 11,
                    "last_played_ts_ms": {{nowSec * 1000}},
                    "device_id": "pixel9"
                  }
                ]
                """).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

            var merge1 = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(dbPath, payload, "pixel9");
            Assert.Equal(2, merge1.MergedTracks);
            Assert.Equal(11, merge1.EffectiveCounts["uid_scrobbled"]);
            Assert.Equal(11, merge1.EffectiveCounts["uid_offline"]);

            // Subsequent Last.fm pull returns the 1 scrobbled play of Blue in Green and top_tracks=11 -> must NOT double-count!
            LastFmLedgerService.IngestPulledCloudPlays(
                dbPath,
                dotTonewise,
                [new CloudRecentTrack("Miles Davis", "Blue in Green", "Kind of Blue", nowSec)]);
            LastFmLedgerService.SyncTopTrackPlayCounts(
                dbPath,
                [new CloudTopTrack("Miles Davis", "Blue in Green", 11)]);

            // Simulate dropped response on pixel9 for uid_offline + 1 extra offline play:
            // Retry sends delta=2, device_play_count_total=12 with a NEW event_id -> only +1 remaining delta must apply!
            var retrySameTotal = JsonDocument.Parse("""
                [{
                  "event_id": "retry_same_total_ev",
                  "track_uid": "uid_offline",
                  "play_count_delta": 1,
                  "scrobbled_delta": 0,
                  "device_play_count_total": 11,
                  "device_id": "pixel9"
                }]
                """).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            var retryRes1 = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(dbPath, retrySameTotal, "pixel9");
            Assert.Equal(0, retryRes1.MergedTracks);

            var retryPlusOne = JsonDocument.Parse("""
                [{
                  "event_id": "retry_plus_one_ev",
                  "track_uid": "uid_offline",
                  "play_count_delta": 2,
                  "scrobbled_delta": 0,
                  "device_play_count_total": 12,
                  "device_id": "pixel9"
                }]
                """).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            var retryRes2 = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(dbPath, retryPlusOne, "pixel9");
            Assert.Equal(1, retryRes2.MergedTracks);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q1 = conn.CreateCommand();
                q1.CommandText = "SELECT lastfm_pulled_count, local_unsynced_expired_count, play_count_total FROM tracks WHERE track_uid = 'uid_scrobbled';";
                using (var r1 = q1.ExecuteReader())
                {
                    Assert.True(r1.Read());
                    Assert.Equal(11, r1.GetInt32(0));
                    Assert.Equal(0, r1.GetInt32(1));
                    Assert.Equal(11, r1.GetInt32(2));
                }

                using var q2 = conn.CreateCommand();
                q2.CommandText = "SELECT lastfm_pulled_count, local_unsynced_expired_count, play_count_total FROM tracks WHERE track_uid = 'uid_offline';";
                using (var r2 = q2.ExecuteReader())
                {
                    Assert.True(r2.Read());
                    Assert.Equal(10, r2.GetInt32(0));
                    Assert.Equal(2, r2.GetInt32(1));
                    Assert.Equal(12, r2.GetInt32(2));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LastFmWebApi_LoopD_RecentBeforeTopTracks_NoDoubleAdd_AndIdleSyncDoesNotRefetch()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b1_loopd_" + Guid.NewGuid().ToString("N"));
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
                        title, artists_json
                    ) VALUES (
                        'uid_goldberg', X'090A', 64, X'0B0C', 256,
                        'Bach/01.flac', 1000, 1000, 'flac', 44100, 200000,
                        'Aria', '["Glenn Gould"]'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int phase = 1;
            int recentCalls = 0;
            bool sawFromZeroOnIdle = false;

            var handler = new StubJsonHandler(req =>
            {
                string q = req.RequestUri?.Query ?? "";
                if (q.Contains("method=user.getrecenttracks", StringComparison.OrdinalIgnoreCase))
                {
                    recentCalls++;
                    if (phase == 1)
                    {
                        return $$"""
                            {
                              "recenttracks": {
                                "@attr": { "totalPages": "1" },
                                "track": [
                                  { "name": "Aria", "artist": { "#text": "Glenn Gould" }, "album": { "#text": "" }, "date": { "uts": "{{nowSec - 500}}" } },
                                  { "name": "Aria", "artist": { "#text": "Glenn Gould" }, "album": { "#text": "" }, "date": { "uts": "{{nowSec - 300}}" } }
                                ]
                              }
                            }
                            """;
                    }
                    if (phase == 2)
                    {
                        return $$"""
                            {
                              "recenttracks": {
                                "@attr": { "totalPages": "1" },
                                "track": [
                                  { "name": "Aria", "artist": { "#text": "Glenn Gould" }, "album": { "#text": "" }, "date": { "uts": "{{nowSec - 100}}" } },
                                  { "name": "Aria", "artist": { "#text": "Glenn Gould" }, "album": { "#text": "" }, "date": { "uts": "{{nowSec - 50}}" } }
                                ]
                              }
                            }
                            """;
                    }
                    if (!q.Contains("&from=", StringComparison.OrdinalIgnoreCase))
                    {
                        sawFromZeroOnIdle = true;
                    }
                    return """{ "recenttracks": { "@attr": { "totalPages": "1" }, "track": [] } }""";
                }
                if (q.Contains("method=user.gettoptracks", StringComparison.OrdinalIgnoreCase))
                {
                    int totalTop = phase == 1 ? 50 : 52;
                    return $$"""
                        {
                          "toptracks": {
                            "@attr": { "totalPages": "1" },
                            "track": [
                              { "name": "Aria", "playcount": "{{totalTop}}", "artist": { "name": "Glenn Gould" } }
                            ]
                          }
                        }
                        """;
                }
                if (q.Contains("method=user.getlovedtracks", StringComparison.OrdinalIgnoreCase))
                {
                    return """{ "lovedtracks": { "@attr": { "totalPages": "1" }, "track": [] } }""";
                }
                return "{}";
            });

            using var http = new HttpClient(handler);
            var client = new LastFmWebApiClient(http);
            var cfg1 = new LastFmAccountConfig("k", "", "u", "", LastPullUnixSec: 0);

            // Sync #1: 2 recent + topTracks=50 -> must land on 50 (NOT 52!)
            await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, cfg1);
            Assert.Equal(50, ReadTrackTotal(dbPath, "uid_goldberg"));

            // Sync #2: 2 new recent + topTracks=52 -> must land on 52 (NOT 54!)
            phase = 2;
            var cfg2 = LastFmAccountConfig.Load(dotTonewise)!;
            await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, cfg2);
            Assert.Equal(52, ReadTrackTotal(dbPath, "uid_goldberg"));

            // Sync #3 (idle): recent=[] with normal non-future LastPullUnixSec -> must NOT fallback to from=0!
            phase = 3;
            recentCalls = 0;
            var cfg3 = LastFmAccountConfig.Load(dotTonewise)!;
            await client.RunFullBidirectionalSyncAsync(dbPath, dotTonewise, cfg3);
            Assert.Equal(1, recentCalls);
            Assert.False(sawFromZeroOnIdle);

            Assert.False(LastFmWebApiClient.ShouldRecoverFutureWatermark(nowSec - 60, nowSec, recentCount: 0));
            Assert.False(LastFmWebApiClient.ShouldRecoverFutureWatermark(0, nowSec, recentCount: 0));
            Assert.True(LastFmWebApiClient.ShouldRecoverFutureWatermark(nowSec + 600, nowSec, recentCount: 0));
            Assert.False(LastFmWebApiClient.ShouldRecoverFutureWatermark(nowSec + 600, nowSec, recentCount: 1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static int ReadTrackTotal(string dbPath, string trackUid)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT play_count_total FROM tracks WHERE track_uid = $uid;";
        cmd.Parameters.AddWithValue("$uid", trackUid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class StubJsonHandler(Func<HttpRequestMessage, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request), Encoding.UTF8, "application/json")
            });
    }
}
