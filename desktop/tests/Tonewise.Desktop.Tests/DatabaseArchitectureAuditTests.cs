using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Services;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DatabaseArchitectureAuditTests
{
    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ComputeSnapshotSha256(string dbPath)
    {
        string snap = Path.Combine(Path.GetTempPath(), $"tw_audit_snap_{Guid.NewGuid():N}.db");
        try
        {
            TonewiseSqlite.CreateConsistentSnapshot(dbPath, snap);
            using var fs = File.OpenRead(snap);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        finally
        {
            try { if (File.Exists(snap)) File.Delete(snap); } catch { }
        }
    }

    [Fact]
    public void ManifestSchemaProvider_EnsureSchemaV2_UpgradesSyntheticV1DatabaseAndPreservesRows()
    {
        string dir = CreateTempDir("tw_v1_v2");
        try
        {
            string dbPath = Path.Combine(dir, "manifest.db");
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO schema_meta (key, value) VALUES ('schema_version', '1');
                    CREATE TABLE tracks (
                        track_uid TEXT PRIMARY KEY,
                        audio_frame_blake3 BLOB NOT NULL UNIQUE,
                        header_size INTEGER NOT NULL DEFAULT 0,
                        header_blake3 BLOB NOT NULL,
                        padding_bytes INTEGER NOT NULL DEFAULT 0,
                        rel_path TEXT NOT NULL,
                        file_size INTEGER NOT NULL,
                        mtime_ms INTEGER NOT NULL,
                        codec TEXT NOT NULL,
                        sample_rate_hz INTEGER NOT NULL,
                        bit_depth INTEGER,
                        bitrate_kbps INTEGER,
                        duration_ms INTEGER NOT NULL,
                        title TEXT NOT NULL,
                        album TEXT,
                        album_artist TEXT,
                        artists_json TEXT NOT NULL DEFAULT '[]',
                        composers_json TEXT NOT NULL DEFAULT '[]',
                        conductors_json TEXT NOT NULL DEFAULT '[]',
                        work_name TEXT,
                        movement_name TEXT,
                        movement_number INTEGER,
                        genre TEXT,
                        year INTEGER,
                        original_date TEXT,
                        release_type TEXT,
                        replaygain_track_db REAL,
                        replaygain_album_db REAL,
                        replaygain_peak REAL,
                        dj_scene_json TEXT NOT NULL DEFAULT '[]',
                        dj_scene_ts INTEGER NOT NULL DEFAULT 0,
                        dj_vocals TEXT,
                        dj_vocals_ts INTEGER NOT NULL DEFAULT 0,
                        dj_energy TEXT,
                        dj_energy_ts INTEGER NOT NULL DEFAULT 0,
                        tempo_bpm REAL,
                        tempo_bucket TEXT,
                        tempo_ts INTEGER NOT NULL DEFAULT 0,
                        mood_json TEXT NOT NULL DEFAULT '[]',
                        mood_ts INTEGER NOT NULL DEFAULT 0,
                        rating INTEGER DEFAULT 0,
                        rating_ts INTEGER NOT NULL DEFAULT 0,
                        play_count_total INTEGER NOT NULL DEFAULT 0,
                        last_played_ts INTEGER NOT NULL DEFAULT 0,
                        waveform_offset INTEGER
                    );
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_blake3, rel_path,
                        file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, play_count_total
                    ) VALUES (
                        'u1', zeroblob(32), zeroblob(32), 'Sym/01.flac',
                        1234, 5678, 'flac', 96000, 300000, 'Allegro', 12
                    );
                    """;
                cmd.ExecuteNonQuery();

                ManifestSchemaProvider.EnsureSchemaV2(conn);

                using var verCmd = conn.CreateCommand();
                verCmd.CommandText = "SELECT value FROM schema_meta WHERE key = 'schema_version';";
                Assert.Equal("2", verCmd.ExecuteScalar()?.ToString());

                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var colCmd = conn.CreateCommand())
                {
                    colCmd.CommandText = "SELECT name FROM pragma_table_info('tracks');";
                    using var r = colCmd.ExecuteReader();
                    while (r.Read()) cols.Add(r.GetString(0));
                }
                Assert.Contains("disc_number", cols);
                Assert.Contains("track_number", cols);
                Assert.Contains("publisher", cols);
                Assert.Contains("legacy_play_count", cols);

                using var idxCmd = conn.CreateCommand();
                idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'tracks';";
                var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var ir = idxCmd.ExecuteReader())
                {
                    while (ir.Read()) indexes.Add(ir.GetString(0));
                }
                Assert.Contains("idx_tracks_album", indexes);
                Assert.Contains("idx_tracks_genre", indexes);
                Assert.Contains("idx_tracks_work_name", indexes);

                using var cntCmd = conn.CreateCommand();
                cntCmd.CommandText = "SELECT play_count_total FROM tracks WHERE track_uid = 'u1';";
                Assert.Equal(12L, Convert.ToInt64(cntCmd.ExecuteScalar()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DesktopStateStore_MigratesLegacyOperationalTablesOutOfManifest_AndKeepsManifestHashStable()
    {
        string dir = CreateTempDir("tw_state_iso");
        try
        {
            string tonewiseDir = Path.Combine(dir, ".tonewise");
            Directory.CreateDirectory(tonewiseDir);
            string manifestPath = Path.Combine(tonewiseDir, "manifest.db");
            string expectedStatePath = Path.Combine(tonewiseDir, "desktop_state.db");
            Assert.Equal(expectedStatePath, DesktopStateStore.ResolveStateDbPath(manifestPath));

            byte[] audioBytes = new byte[32];
            audioBytes[0] = 0xAA;
            using (var conn = TonewiseSqlite.OpenReadWrite(manifestPath))
            {
                using var init = conn.CreateCommand();
                init.CommandText = ManifestSchemaProvider.ManifestSql;
                init.ExecuteNonQuery();

                using var seed = conn.CreateCommand();
                seed.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_blake3, rel_path,
                        file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES ('uid_01', $af, zeroblob(32), 'Bach/01.flac', 1000, 1000, 'flac', 44100, 120000, 'Prelude');

                    CREATE TABLE staging_diff_queue (
                        proposal_id TEXT PRIMARY KEY, track_uid TEXT NOT NULL, field_name TEXT NOT NULL,
                        original_value TEXT, suggested_value TEXT NOT NULL, confidence REAL NOT NULL,
                        source_tag TEXT NOT NULL, status TEXT NOT NULL, batch_id TEXT, created_at_ms INTEGER NOT NULL
                    );
                    INSERT INTO staging_diff_queue VALUES (
                        'uid_01:dj_energy', 'uid_01', 'dj_energy', NULL, 'low', 0.9, 'Test', 'Pending', NULL, 1000
                    );
                    CREATE TABLE lan_sync_play_acks (
                        event_key TEXT PRIMARY KEY, track_uid TEXT NOT NULL, delta INTEGER NOT NULL, applied_at_ms INTEGER NOT NULL
                    );
                    INSERT INTO lan_sync_play_acks VALUES ('dev1::ev1', 'uid_01', 1, 9999999999999);
                    """;
                seed.Parameters.AddWithValue("$af", audioBytes);
                seed.ExecuteNonQuery();
            }

            // Opening StagingDiffStore migrates & drops legacy tables from manifest.db into desktop_state.db.
            var store = new StagingDiffStore(manifestPath, dir);
            Assert.Equal(expectedStatePath, store.StateDbPath);
            Assert.True(File.Exists(expectedStatePath));

            using (var mCheck = TonewiseSqlite.OpenReadOnly(manifestPath))
            {
                using var cmd = mCheck.CreateCommand();
                cmd.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name IN ('staging_diff_queue', 'curation_batches', 'lan_sync_play_acks', 'lan_sync_device_watermarks');
                    """;
                Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            }

            Assert.Single(store.GetPendingProposals());

            string hashBeforeStaging = ComputeSnapshotSha256(manifestPath);

            // Adding new staging proposals & approvals must NOT mutate manifest.db content hash.
            var extraProposal = StagingDiffStore.TryCreateProposal("uid_01", "dj_vocals", null, "instrumental", 0.95, "Test")!;
            Assert.Equal(1, store.EnqueueProposals([extraProposal]));
            Assert.Equal(2, store.GetPendingProposals().Count);

            string hashAfterStaging = ComputeSnapshotSha256(manifestPath);
            Assert.Equal(hashBeforeStaging, hashAfterStaging);

            // Idempotent duplicate LAN report event (already in migrated acks) also must not mutate manifest.db.
            using var dupJson = JsonDocument.Parse("""[{"track_uid":"uid_01","play_count_delta":1,"event_id":"ev1"}]""");
            var mergeRes = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(
                manifestPath, dupJson.RootElement.EnumerateArray(), deviceId: "dev1");
            Assert.Equal(0, mergeRes.MergedTracks);
            Assert.Equal(hashBeforeStaging, ComputeSnapshotSha256(manifestPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DesktopStateStore_PruneOldLanSyncAcks_RemovesExpiredAndCapsRows()
    {
        string dir = CreateTempDir("tw_prune_acks");
        try
        {
            string statePath = Path.Combine(dir, "desktop_state.db");
            using var conn = TonewiseSqlite.OpenReadWrite(statePath);
            DesktopStateStore.EnsureSchema(conn);

            long nowMs = 1_763_300_000_000L;
            using (var tx = TonewiseSqlite.BeginImmediateTransaction(conn))
            {
                void InsertAck(string key, long appliedAtMs)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO lan_sync_play_acks (event_key, track_uid, delta, applied_at_ms) VALUES ($k, 'u1', 1, $t);";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$t", appliedAtMs);
                    cmd.ExecuteNonQuery();
                }

                // Expired (> 90 days old)
                InsertAck("expired_1", nowMs - 100L * 86_400_000L);
                // Recent valid rows
                InsertAck("recent_1", nowMs - 3_000L);
                InsertAck("recent_2", nowMs - 2_000L);
                InsertAck("recent_3", nowMs - 1_000L);

                int deleted = DesktopStateStore.PruneOldLanSyncAcks(conn, tx, nowMs, retentionDays: 90, maxRows: 2);
                Assert.Equal(2, deleted); // 1 expired + 1 oldest recent trimmed to satisfy maxRows=2
                tx.Commit();
            }

            using var verify = conn.CreateCommand();
            verify.CommandText = "SELECT event_key FROM lan_sync_play_acks ORDER BY applied_at_ms ASC;";
            var kept = new List<string>();
            using var r = verify.ExecuteReader();
            while (r.Read()) kept.Add(r.GetString(0));
            Assert.Equal(["recent_2", "recent_3"], kept);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LibraryStore_ReadsDiscAndTrackNumbersFromManifestWithRelPathFallback()
    {
        string dir = CreateTempDir("tw_lib_disc_track");
        try
        {
            string tonewiseDir = Path.Combine(dir, ".tonewise");
            Directory.CreateDirectory(tonewiseDir);
            string manifestPath = Path.Combine(tonewiseDir, "manifest.db");

            using (var conn = TonewiseSqlite.OpenReadWrite(manifestPath))
            {
                using var init = conn.CreateCommand();
                init.CommandText = ManifestSchemaProvider.ManifestSql;
                init.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_blake3, rel_path,
                        file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title,
                        disc_number, track_number
                    ) VALUES
                        ('u_db', zeroblob(32), zeroblob(32), 'Mozart/Unnumbered.flac', 1000, 1000, 'flac', 44100, 120000, 'Movement I', 2, 7),
                        ('u_path', X'0000000000000000000000000000000000000000000000000000000000000001', zeroblob(32), 'Mozart/CD3/04 Finale.flac', 1000, 1000, 'flac', 44100, 120000, 'Movement IV', 0, 0);
                    """;
                ins.ExecuteNonQuery();
            }

            var loaded = new LibraryStore().LoadFromMusicRoot(dir).ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
            Assert.Equal(2, loaded["u_db"].DiscNumber);
            Assert.Equal(7, loaded["u_db"].TrackNumber);
            Assert.Equal(3, loaded["u_path"].DiscNumber);
            Assert.Equal(4, loaded["u_path"].TrackNumber);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
