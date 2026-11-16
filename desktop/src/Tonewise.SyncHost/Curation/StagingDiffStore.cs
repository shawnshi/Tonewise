using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tonewise.Interop;

namespace Tonewise.SyncHost.Curation;

public static class ProposalStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Committed = "Committed";
    public const string RolledBack = "RolledBack";
}

public sealed record TagDiffProposal(
    string ProposalId,
    string TrackUid,
    string FieldName,
    string? OriginalValue,
    string SuggestedValue,
    double Confidence,
    string SourceTag,
    string Status = ProposalStatus.Pending,
    string? BatchId = null,
    long CreatedAtMs = 0
);

public sealed record CurationCommitResult(string BatchId, int CommittedCount, int PhysicalFilesUpdated = 0);

/// <summary>
/// Enforces the two anti-corruption curation laws against `manifest.db`:
/// 1) Fill-Missing Only (`IsFieldMissing`) unless explicitly unlocked;
/// 2) Staging Diff Queue preview + transactional batch commit and one-click rollback.
/// </summary>
public sealed class StagingDiffStore
{
    private static readonly IReadOnlyDictionary<string, string> FieldToColumnMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dj_scene"] = "dj_scene_json",
            ["dj_scene_json"] = "dj_scene_json",
            ["dj_vocals"] = "dj_vocals",
            ["dj_energy"] = "dj_energy",
            ["tempo_bpm"] = "tempo_bpm",
            ["tempo_bucket"] = "tempo_bucket",
            ["mood"] = "mood_json",
            ["mood_json"] = "mood_json",
            ["genre"] = "genre",
            ["album"] = "album",
            ["year"] = "year",
            ["publisher"] = "publisher",
        };

    private static readonly IReadOnlyDictionary<string, string> FieldToTimestampColumnMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dj_scene_json"] = "dj_scene_ts",
            ["dj_vocals"] = "dj_vocals_ts",
            ["dj_energy"] = "dj_energy_ts",
            ["tempo_bpm"] = "tempo_ts",
            ["tempo_bucket"] = "tempo_ts",
            ["mood_json"] = "mood_ts",
        };

    public string DbPath { get; }
    public string? MusicRoot { get; }

    public StagingDiffStore(string manifestDbPath, string? musicRoot = null)
    {
        DbPath = manifestDbPath;
        MusicRoot = musicRoot ?? InferMusicRootFromDbPath(manifestDbPath);
        EnsureSchema();
    }

    private static string? InferMusicRootFromDbPath(string dbPath)
    {
        string? dotDir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dotDir)) return null;
        string name = Path.GetFileName(dotDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, ".tonewise", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(dotDir)?.FullName
            : Path.GetDirectoryName(dbPath);
    }

    public static bool IsFieldMissing(string? currentValue)
    {
        if (string.IsNullOrWhiteSpace(currentValue)) return true;
        string trimmed = currentValue.Trim();
        return trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
               || trimmed == "[]"
               || trimmed == "0";
    }

    public static TagDiffProposal? TryCreateProposal(
        string trackUid,
        string fieldName,
        string? originalValue,
        string suggestedValue,
        double confidence,
        string sourceTag,
        bool forceOverwriteUnlocked = false)
    {
        if (string.IsNullOrWhiteSpace(suggestedValue)) return null;
        if (!forceOverwriteUnlocked && !IsFieldMissing(originalValue)) return null;

        string normalizedField = ResolveColumnName(fieldName);
        return new TagDiffProposal(
            ProposalId: $"{trackUid}:{normalizedField}",
            TrackUid: trackUid,
            FieldName: normalizedField,
            OriginalValue: originalValue,
            SuggestedValue: suggestedValue,
            Confidence: Math.Clamp(confidence, 0.0, 1.0),
            SourceTag: sourceTag,
            Status: ProposalStatus.Pending,
            BatchId: null,
            CreatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    public void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS staging_diff_queue (
                proposal_id   TEXT PRIMARY KEY,
                track_uid     TEXT NOT NULL,
                field_name    TEXT NOT NULL,
                original_value TEXT,
                suggested_value TEXT NOT NULL,
                confidence    REAL NOT NULL,
                source_tag    TEXT NOT NULL,
                status        TEXT NOT NULL,
                batch_id      TEXT,
                created_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS curation_batches (
                batch_id       TEXT PRIMARY KEY,
                committed_at_ms INTEGER NOT NULL,
                item_count     INTEGER NOT NULL,
                rolled_back    INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        EnsureOptionalTrackColumns(conn);
    }

    public int EnqueueProposals(IEnumerable<TagDiffProposal> proposals, bool forceOverwriteUnlocked = false)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        int inserted = 0;

        foreach (var p in proposals)
        {
            if (string.IsNullOrWhiteSpace(p.SuggestedValue)) continue;
            if (!forceOverwriteUnlocked && !IsFieldMissing(p.OriginalValue)) continue;

            string col = ResolveColumnName(p.FieldName);
            long ts = p.CreatedAtMs > 0 ? p.CreatedAtMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string id = string.IsNullOrWhiteSpace(p.ProposalId) ? $"{p.TrackUid}:{col}" : p.ProposalId;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO staging_diff_queue (
                    proposal_id, track_uid, field_name, original_value, suggested_value,
                    confidence, source_tag, status, batch_id, created_at_ms
                ) VALUES ($id, $uid, $field, $orig, $sugg, $conf, $src, $status, NULL, $ts)
                ON CONFLICT(proposal_id) DO UPDATE SET
                    original_value = excluded.original_value,
                    suggested_value = excluded.suggested_value,
                    confidence = excluded.confidence,
                    source_tag = excluded.source_tag,
                    status = $status,
                    batch_id = NULL,
                    created_at_ms = excluded.created_at_ms;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uid", p.TrackUid);
            cmd.Parameters.AddWithValue("$field", col);
            cmd.Parameters.AddWithValue("$orig", (object?)p.OriginalValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sugg", p.SuggestedValue);
            cmd.Parameters.AddWithValue("$conf", p.Confidence);
            cmd.Parameters.AddWithValue("$src", p.SourceTag);
            cmd.Parameters.AddWithValue("$status", ProposalStatus.Pending);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();
            inserted++;
        }

        tx.Commit();
        return inserted;
    }

    public IReadOnlyList<TagDiffProposal> GetPendingProposals()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                   confidence, source_tag, status, batch_id, created_at_ms
            FROM staging_diff_queue
            WHERE status IN ('Pending', 'Approved')
            ORDER BY confidence DESC, created_at_ms DESC;
            """;

        var list = new List<TagDiffProposal>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TagDiffProposal(
                ProposalId: reader.GetString(0),
                TrackUid: reader.GetString(1),
                FieldName: reader.GetString(2),
                OriginalValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                SuggestedValue: reader.GetString(4),
                Confidence: reader.GetDouble(5),
                SourceTag: reader.GetString(6),
                Status: reader.GetString(7),
                BatchId: reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAtMs: reader.GetInt64(9)
            ));
        }
        return list;
    }

    public void SetProposalApproval(string proposalId, bool approved)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE staging_diff_queue SET status = $st WHERE proposal_id = $id;";
        cmd.Parameters.AddWithValue("$st", approved ? ProposalStatus.Approved : ProposalStatus.Rejected);
        cmd.Parameters.AddWithValue("$id", proposalId);
        cmd.ExecuteNonQuery();
    }

    public CurationCommitResult CommitApprovedBatch(string? customBatchId = null, double autoApproveThreshold = 0.80)
    {
        string batchId = string.IsNullOrWhiteSpace(customBatchId)
            ? $"batch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : customBatchId;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = OpenConnection();
        EnsureOptionalTrackColumns(conn);
        using var tx = conn.BeginTransaction();

        var targets = new List<TagDiffProposal>();
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                       confidence, source_tag, status, batch_id, created_at_ms
                FROM staging_diff_queue
                WHERE status = 'Approved' OR (status = 'Pending' AND confidence >= $thresh);
                """;
            selectCmd.Parameters.AddWithValue("$thresh", autoApproveThreshold);
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                targets.Add(new TagDiffProposal(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetDouble(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt64(9)
                ));
            }
        }

        if (targets.Count == 0)
        {
            return new CurationCommitResult(batchId, 0);
        }

        int committed = 0;
        var updatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var item in targets)
        {
            string column = ResolveColumnName(item.FieldName);
            ApplyTrackColumnValue(conn, tx, item.TrackUid, column, item.SuggestedValue, nowMs);

            if (!updatesByTrack.TryGetValue(item.TrackUid, out var trackUpdates))
            {
                trackUpdates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                updatesByTrack[item.TrackUid] = trackUpdates;
            }
            trackUpdates[column] = item.SuggestedValue;

            using var markCmd = conn.CreateCommand();
            markCmd.Transaction = tx;
            markCmd.CommandText = "UPDATE staging_diff_queue SET status = $st, batch_id = $bid WHERE proposal_id = $id;";
            markCmd.Parameters.AddWithValue("$st", ProposalStatus.Committed);
            markCmd.Parameters.AddWithValue("$bid", batchId);
            markCmd.Parameters.AddWithValue("$id", item.ProposalId);
            markCmd.ExecuteNonQuery();
            committed++;
        }

        using (var batchCmd = conn.CreateCommand())
        {
            batchCmd.Transaction = tx;
            batchCmd.CommandText = """
                INSERT INTO curation_batches (batch_id, committed_at_ms, item_count, rolled_back)
                VALUES ($bid, $ts, $cnt, 0);
                """;
            batchCmd.Parameters.AddWithValue("$bid", batchId);
            batchCmd.Parameters.AddWithValue("$ts", nowMs);
            batchCmd.Parameters.AddWithValue("$cnt", committed);
            batchCmd.ExecuteNonQuery();
        }

        int physicalUpdated = WriteBackUpdatesToPhysicalFiles(conn, tx, updatesByTrack);

        tx.Commit();
        return new CurationCommitResult(batchId, committed, physicalUpdated);
    }

    public int RollbackBatch(string batchId)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenConnection();
        EnsureOptionalTrackColumns(conn);
        using var tx = conn.BeginTransaction();

        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT rolled_back FROM curation_batches WHERE batch_id = $bid;";
            checkCmd.Parameters.AddWithValue("$bid", batchId);
            object? res = checkCmd.ExecuteScalar();
            if (res is null || Convert.ToInt64(res) != 0)
            {
                return 0;
            }
        }

        var items = new List<(string ProposalId, string TrackUid, string FieldName, string? OriginalValue)>();
        using (var listCmd = conn.CreateCommand())
        {
            listCmd.Transaction = tx;
            listCmd.CommandText = """
                SELECT proposal_id, track_uid, field_name, original_value
                FROM staging_diff_queue
                WHERE batch_id = $bid AND status = 'Committed';
                """;
            listCmd.Parameters.AddWithValue("$bid", batchId);
            using var reader = listCmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                ));
            }
        }

        var rollbackUpdatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string column = ResolveColumnName(item.FieldName);
            ApplyTrackColumnValue(conn, tx, item.TrackUid, column, item.OriginalValue, nowMs);

            if (!rollbackUpdatesByTrack.TryGetValue(item.TrackUid, out var trackUpdates))
            {
                trackUpdates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                rollbackUpdatesByTrack[item.TrackUid] = trackUpdates;
            }
            trackUpdates[column] = item.OriginalValue;

            using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = "UPDATE staging_diff_queue SET status = $st WHERE proposal_id = $id;";
            updCmd.Parameters.AddWithValue("$st", ProposalStatus.RolledBack);
            updCmd.Parameters.AddWithValue("$id", item.ProposalId);
            updCmd.ExecuteNonQuery();
        }

        WriteBackUpdatesToPhysicalFiles(conn, tx, rollbackUpdatesByTrack);

        using (var markBatchCmd = conn.CreateCommand())
        {
            markBatchCmd.Transaction = tx;
            markBatchCmd.CommandText = "UPDATE curation_batches SET rolled_back = 1 WHERE batch_id = $bid;";
            markBatchCmd.Parameters.AddWithValue("$bid", batchId);
            markBatchCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return items.Count;
    }

    private int WriteBackUpdatesToPhysicalFiles(
        SqliteConnection conn,
        SqliteTransaction tx,
        Dictionary<string, Dictionary<string, string?>> updatesByTrack)
    {
        if (string.IsNullOrWhiteSpace(MusicRoot) || updatesByTrack.Count == 0)
            return 0;

        int updatedFiles = 0;
        foreach (var (uid, updates) in updatesByTrack)
        {
            string? relPath;
            using (var pathCmd = conn.CreateCommand())
            {
                pathCmd.Transaction = tx;
                pathCmd.CommandText = "SELECT rel_path FROM tracks WHERE track_uid = $uid;";
                pathCmd.Parameters.AddWithValue("$uid", uid);
                relPath = pathCmd.ExecuteScalar() as string;
            }
            if (string.IsNullOrWhiteSpace(relPath)) continue;

            string fullPath = Path.Combine(MusicRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;

            try
            {
                var fp = TonewiseCoreClient.WriteAudioTags(fullPath, updates);
                byte[] newHeaderHash = Convert.FromHexString(fp.HeaderBlake3Hex);

                using var syncCmd = conn.CreateCommand();
                syncCmd.Transaction = tx;
                syncCmd.CommandText = """
                    UPDATE tracks
                    SET header_size = $hsize,
                        header_blake3 = $hhash,
                        file_size = $fsize
                    WHERE track_uid = $uid;
                    """;
                syncCmd.Parameters.AddWithValue("$hsize", (long)fp.HeaderSize);
                syncCmd.Parameters.AddWithValue("$hhash", newHeaderHash);
                syncCmd.Parameters.AddWithValue("$fsize", (long)fp.FileSize);
                syncCmd.Parameters.AddWithValue("$uid", uid);
                syncCmd.ExecuteNonQuery();
                updatedFiles++;
            }
            catch
            {
                // Skip container writeback when file is missing, read-only, or non-container stub
            }
        }
        return updatedFiles;
    }

    private static string ResolveColumnName(string fieldName)
    {
        if (!FieldToColumnMap.TryGetValue(fieldName.Trim(), out string? col))
        {
            throw new ArgumentException($"Unsupported curation field '{fieldName}'", nameof(fieldName));
        }
        return col;
    }

    private static void ApplyTrackColumnValue(
        SqliteConnection conn,
        SqliteTransaction tx,
        string trackUid,
        string column,
        string? rawValue,
        long lwwTsMs)
    {
        object dbVal = rawValue is null
            ? (column.EndsWith("_json", StringComparison.Ordinal) ? "[]" : DBNull.Value)
            : CoerceColumnValue(column, rawValue);

        string sql = FieldToTimestampColumnMap.TryGetValue(column, out string? tsCol)
            ? $"UPDATE tracks SET {column} = $val, {tsCol} = $ts WHERE track_uid = $uid;"
            : $"UPDATE tracks SET {column} = $val WHERE track_uid = $uid;";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$val", dbVal);
        cmd.Parameters.AddWithValue("$uid", trackUid);
        if (tsCol is not null)
        {
            cmd.Parameters.AddWithValue("$ts", lwwTsMs);
        }
        cmd.ExecuteNonQuery();
    }

    private static object CoerceColumnValue(string column, string rawValue)
    {
        if (column == "year" && int.TryParse(rawValue, out int y))
            return y;
        if (column == "tempo_bpm" && double.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bpm))
            return bpm;
        if (column.EndsWith("_json", StringComparison.Ordinal))
        {
            string trimmed = rawValue.Trim();
            if (trimmed.StartsWith('[')) return trimmed;
            var parts = trimmed.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return JsonSerializer.Serialize(parts);
        }
        return rawValue;
    }

    private static void EnsureOptionalTrackColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM pragma_table_info('tracks');";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        if (existing.Count == 0) return;

        (string Name, string AlterSql)[] optionalCols =
        [
            ("publisher", "ALTER TABLE tracks ADD COLUMN publisher TEXT;"),
            ("legacy_play_count", "ALTER TABLE tracks ADD COLUMN legacy_play_count INTEGER NOT NULL DEFAULT 0;"),
            ("lastfm_pulled_count", "ALTER TABLE tracks ADD COLUMN lastfm_pulled_count INTEGER NOT NULL DEFAULT 0;"),
            ("local_unsynced_expired_count", "ALTER TABLE tracks ADD COLUMN local_unsynced_expired_count INTEGER NOT NULL DEFAULT 0;"),
        ];

        foreach (var (name, sql) in optionalCols)
        {
            if (!existing.Contains(name))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }
}
