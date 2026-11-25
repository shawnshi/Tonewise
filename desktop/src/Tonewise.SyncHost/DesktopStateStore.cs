using Microsoft.Data.Sqlite;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost;

/// <summary>
/// Manages PC-local operational state (<c>desktop_state.db</c>) separated from the synced
/// canonical catalog (<c>manifest.db</c>): curation diff staging queue, batch audit logs,
/// LAN sync replay acks, and per-device watermarks.
/// </summary>
public static class DesktopStateStore
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultAckRetentionDays = 90;
    public const int DefaultAckMaxRows = 50_000;

    private static readonly string[] LegacyOperationalTables =
    [
        "staging_diff_queue",
        "curation_batches",
        "lan_sync_play_acks",
        "lan_sync_device_watermarks",
    ];

    public static string ResolveStateDbPath(string manifestDbPath)
    {
        if (string.IsNullOrWhiteSpace(manifestDbPath))
            throw new ArgumentException("Manifest DB path is required.", nameof(manifestDbPath));

        string full = Path.GetFullPath(manifestDbPath);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string fileName = Path.GetFileName(full);
        string stateFileName = string.Equals(fileName, "manifest.db", StringComparison.OrdinalIgnoreCase)
            ? "desktop_state.db"
            : Path.GetFileNameWithoutExtension(fileName) + ".state.db";
        return Path.Combine(dir, stateFileName);
    }

    public static void EnsureSchema(string stateDbPath)
    {
        using var conn = TonewiseSqlite.OpenReadWrite(stateDbPath);
        EnsureSchema(conn);
    }

    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS state_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO state_meta (key, value) VALUES ('schema_version', '1');

            CREATE TABLE IF NOT EXISTS staging_diff_queue (
                proposal_id     TEXT PRIMARY KEY,
                track_uid       TEXT NOT NULL,
                field_name      TEXT NOT NULL,
                original_value  TEXT,
                suggested_value TEXT NOT NULL,
                confidence      REAL NOT NULL,
                source_tag      TEXT NOT NULL,
                status          TEXT NOT NULL,
                batch_id        TEXT,
                created_at_ms   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_staging_status ON staging_diff_queue (status, confidence DESC);
            CREATE INDEX IF NOT EXISTS idx_staging_batch ON staging_diff_queue (batch_id);
            CREATE INDEX IF NOT EXISTS idx_staging_track ON staging_diff_queue (track_uid);

            CREATE TABLE IF NOT EXISTS curation_batches (
                batch_id        TEXT PRIMARY KEY,
                committed_at_ms INTEGER NOT NULL,
                item_count      INTEGER NOT NULL,
                rolled_back     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_curation_batches_time ON curation_batches (committed_at_ms DESC);

            CREATE TABLE IF NOT EXISTS lan_sync_play_acks (
                event_key     TEXT PRIMARY KEY,
                track_uid     TEXT NOT NULL,
                delta         INTEGER NOT NULL,
                applied_at_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_lan_sync_acks_applied ON lan_sync_play_acks (applied_at_ms);
            CREATE INDEX IF NOT EXISTS idx_lan_sync_acks_track ON lan_sync_play_acks (track_uid);

            CREATE TABLE IF NOT EXISTS lan_sync_device_watermarks (
                device_id         TEXT NOT NULL,
                track_uid         TEXT NOT NULL,
                last_device_total INTEGER NOT NULL,
                updated_at_ms     INTEGER NOT NULL,
                PRIMARY KEY (device_id, track_uid)
            );
            CREATE INDEX IF NOT EXISTS idx_lan_sync_wm_track ON lan_sync_device_watermarks (track_uid);

            CREATE TABLE IF NOT EXISTS quickplay_pins (
                pin_id       TEXT PRIMARY KEY,
                kind         TEXT NOT NULL,
                target_key   TEXT NOT NULL,
                title        TEXT NOT NULL,
                subtitle     TEXT NOT NULL DEFAULT '',
                cover_path   TEXT,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                pinned_at_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_quickplay_pins_order ON quickplay_pins (sort_order ASC, pinned_at_ms DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time idempotent migration that copies any legacy PC operational tables out of
    /// <c>manifest.db</c> into <c>desktop_state.db</c> and drops them from <c>manifest.db</c> so
    /// catalog snapshots and <c>manifest_sha256</c> never drift from curation or sync ack writes.
    /// </summary>
    public static void MigrateLegacyOperationalTablesIfPresent(string manifestDbPath, string stateDbPath)
    {
        if (!File.Exists(manifestDbPath)) return;

        var foundTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probeConn = TonewiseSqlite.OpenReadOnly(manifestDbPath))
        {
            using var probeCmd = probeConn.CreateCommand();
            probeCmd.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('staging_diff_queue', 'curation_batches', 'lan_sync_play_acks', 'lan_sync_device_watermarks');
                """;
            using var r = probeCmd.ExecuteReader();
            while (r.Read())
            {
                foundTables.Add(r.GetString(0));
            }
        }

        if (foundTables.Count == 0) return;

        using var stateConn = TonewiseSqlite.OpenReadWrite(stateDbPath);
        EnsureSchema(stateConn);

        using (var srcConn = TonewiseSqlite.OpenReadOnly(manifestDbPath))
        {
            using var stateTx = TonewiseSqlite.BeginImmediateTransaction(stateConn);

            if (foundTables.Contains("staging_diff_queue"))
            {
                CopyTableRows(srcConn, stateConn, stateTx,
                    """
                    SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                           confidence, source_tag, status, batch_id, created_at_ms
                    FROM staging_diff_queue;
                    """,
                    """
                    INSERT OR IGNORE INTO staging_diff_queue (
                        proposal_id, track_uid, field_name, original_value, suggested_value,
                        confidence, source_tag, status, batch_id, created_at_ms
                    ) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9);
                    """,
                    10);
            }

            if (foundTables.Contains("curation_batches"))
            {
                CopyTableRows(srcConn, stateConn, stateTx,
                    "SELECT batch_id, committed_at_ms, item_count, rolled_back FROM curation_batches;",
                    """
                    INSERT OR IGNORE INTO curation_batches (batch_id, committed_at_ms, item_count, rolled_back)
                    VALUES ($p0, $p1, $p2, $p3);
                    """,
                    4);
            }

            if (foundTables.Contains("lan_sync_play_acks"))
            {
                CopyTableRows(srcConn, stateConn, stateTx,
                    "SELECT event_key, track_uid, delta, applied_at_ms FROM lan_sync_play_acks;",
                    """
                    INSERT OR IGNORE INTO lan_sync_play_acks (event_key, track_uid, delta, applied_at_ms)
                    VALUES ($p0, $p1, $p2, $p3);
                    """,
                    4);
            }

            if (foundTables.Contains("lan_sync_device_watermarks"))
            {
                CopyTableRows(srcConn, stateConn, stateTx,
                    "SELECT device_id, track_uid, last_device_total, updated_at_ms FROM lan_sync_device_watermarks;",
                    """
                    INSERT INTO lan_sync_device_watermarks (device_id, track_uid, last_device_total, updated_at_ms)
                    VALUES ($p0, $p1, $p2, $p3)
                    ON CONFLICT(device_id, track_uid) DO UPDATE SET
                        last_device_total = MAX(lan_sync_device_watermarks.last_device_total, excluded.last_device_total),
                        updated_at_ms = MAX(lan_sync_device_watermarks.updated_at_ms, excluded.updated_at_ms);
                    """,
                    4);
            }

            using (var metaCmd = stateConn.CreateCommand())
            {
                metaCmd.Transaction = stateTx;
                metaCmd.CommandText = """
                    INSERT INTO state_meta (key, value) VALUES ('legacy_manifest_migrated', '1')
                    ON CONFLICT(key) DO UPDATE SET value = '1';
                    """;
                metaCmd.ExecuteNonQuery();
            }

            stateTx.Commit();
        }

        using var rwManifest = TonewiseSqlite.OpenReadWrite(manifestDbPath);
        using var dropTx = TonewiseSqlite.BeginImmediateTransaction(rwManifest);
        foreach (string tableName in LegacyOperationalTables)
        {
            if (!foundTables.Contains(tableName)) continue;
            using var dropCmd = rwManifest.CreateCommand();
            dropCmd.Transaction = dropTx;
            dropCmd.CommandText = $"DROP TABLE IF EXISTS {tableName};";
            dropCmd.ExecuteNonQuery();
        }
        dropTx.Commit();
    }

    public static int PruneOldLanSyncAcks(
        SqliteConnection stateConn,
        SqliteTransaction? tx,
        long nowMs,
        int retentionDays = DefaultAckRetentionDays,
        int maxRows = DefaultAckMaxRows)
    {
        int deleted = 0;
        long cutoffMs = nowMs - Math.Max(1, retentionDays) * 86_400_000L;

        using (var delExpired = stateConn.CreateCommand())
        {
            delExpired.Transaction = tx;
            delExpired.CommandText = "DELETE FROM lan_sync_play_acks WHERE applied_at_ms < $cutoff;";
            delExpired.Parameters.AddWithValue("$cutoff", cutoffMs);
            deleted += delExpired.ExecuteNonQuery();
        }

        if (maxRows > 0)
        {
            using var countCmd = stateConn.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM lan_sync_play_acks;";
            long total = Convert.ToInt64(countCmd.ExecuteScalar());
            if (total > maxRows)
            {
                long excess = total - maxRows;
                using var trimCmd = stateConn.CreateCommand();
                trimCmd.Transaction = tx;
                trimCmd.CommandText = """
                    DELETE FROM lan_sync_play_acks
                    WHERE event_key IN (
                        SELECT event_key FROM lan_sync_play_acks
                        ORDER BY applied_at_ms ASC
                        LIMIT $excess
                    );
                    """;
                trimCmd.Parameters.AddWithValue("$excess", excess);
                deleted += trimCmd.ExecuteNonQuery();
            }
        }

        return deleted;
    }

    private static void CopyTableRows(
        SqliteConnection srcConn,
        SqliteConnection dstConn,
        SqliteTransaction dstTx,
        string selectSql,
        string insertSql,
        int colCount)
    {
        using var readCmd = srcConn.CreateCommand();
        readCmd.CommandText = selectSql;
        using var reader = readCmd.ExecuteReader();
        while (reader.Read())
        {
            using var insCmd = dstConn.CreateCommand();
            insCmd.Transaction = dstTx;
            insCmd.CommandText = insertSql;
            for (int i = 0; i < colCount; i++)
            {
                object val = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                insCmd.Parameters.AddWithValue($"$p{i}", val);
            }
            insCmd.ExecuteNonQuery();
        }
    }

    public static IReadOnlyList<QuickplayPinRecord> ListQuickplayPins(string stateDbPath)
    {
        if (string.IsNullOrWhiteSpace(stateDbPath) || !File.Exists(stateDbPath))
            return [];

        using var conn = TonewiseSqlite.OpenReadWrite(stateDbPath);
        EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pin_id, kind, target_key, title, subtitle, cover_path, sort_order, pinned_at_ms
            FROM quickplay_pins
            ORDER BY sort_order ASC, pinned_at_ms DESC;
            """;
        var list = new List<QuickplayPinRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new QuickplayPinRecord(
                PinId: reader.GetString(0),
                Kind: reader.GetString(1),
                TargetKey: reader.GetString(2),
                Title: reader.GetString(3),
                Subtitle: reader.IsDBNull(4) ? "" : reader.GetString(4),
                CoverPath: reader.IsDBNull(5) ? null : reader.GetString(5),
                SortOrder: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                PinnedAtMs: reader.IsDBNull(7) ? 0 : reader.GetInt64(7)));
        }
        return list;
    }

    public static void UpsertQuickplayPin(string stateDbPath, QuickplayPinRecord pin)
    {
        if (string.IsNullOrWhiteSpace(stateDbPath)) return;
        using var conn = TonewiseSqlite.OpenReadWrite(stateDbPath);
        EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quickplay_pins (
                pin_id, kind, target_key, title, subtitle, cover_path, sort_order, pinned_at_ms
            ) VALUES (
                $id, $kind, $target, $title, $sub, $cover, $order, $ts
            )
            ON CONFLICT(pin_id) DO UPDATE SET
                kind = excluded.kind,
                target_key = excluded.target_key,
                title = excluded.title,
                subtitle = excluded.subtitle,
                cover_path = excluded.cover_path,
                sort_order = excluded.sort_order,
                pinned_at_ms = excluded.pinned_at_ms;
            """;
        cmd.Parameters.AddWithValue("$id", pin.PinId);
        cmd.Parameters.AddWithValue("$kind", pin.Kind);
        cmd.Parameters.AddWithValue("$target", pin.TargetKey);
        cmd.Parameters.AddWithValue("$title", pin.Title);
        cmd.Parameters.AddWithValue("$sub", pin.Subtitle ?? "");
        cmd.Parameters.AddWithValue("$cover", (object?)pin.CoverPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$order", pin.SortOrder);
        cmd.Parameters.AddWithValue("$ts", pin.PinnedAtMs);
        cmd.ExecuteNonQuery();
    }

    public static bool RemoveQuickplayPin(string stateDbPath, string pinId)
    {
        if (string.IsNullOrWhiteSpace(stateDbPath) || !File.Exists(stateDbPath) || string.IsNullOrWhiteSpace(pinId))
            return false;
        using var conn = TonewiseSqlite.OpenReadWrite(stateDbPath);
        EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM quickplay_pins WHERE pin_id = $id;";
        cmd.Parameters.AddWithValue("$id", pinId);
        return cmd.ExecuteNonQuery() > 0;
    }
}
