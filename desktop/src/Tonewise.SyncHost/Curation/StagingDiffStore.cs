using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tonewise.Interop;
using Tonewise.SyncHost.Workbench;

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

public sealed record CurationCommitResult(
    string BatchId,
    int CommittedCount,
    int PhysicalFilesUpdated = 0,
    IReadOnlyList<string>? CommittedTrackUids = null,
    IReadOnlyList<string>? PhysicalWriteFailures = null);

public sealed record RollbackBatchResult(
    int RolledBackCount,
    IReadOnlyList<string> AffectedTrackUids,
    IReadOnlyDictionary<string, Dictionary<string, string?>> ForwardUpdatesByTrack);

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
            ["rating"] = "rating",
            ["work"] = "work_name",
            ["work_name"] = "work_name",
            ["movement"] = "movement_name",
            ["movement_name"] = "movement_name",
            ["title"] = "title",
            ["album_artist"] = "album_artist",
            ["albumartist"] = "album_artist",
            ["artist"] = "artists_json",
            ["artists"] = "artists_json",
            ["artists_json"] = "artists_json",
            ["composer"] = "composers_json",
            ["composers"] = "composers_json",
            ["composers_json"] = "composers_json",
            ["conductor"] = "conductors_json",
            ["conductors"] = "conductors_json",
            ["conductors_json"] = "conductors_json",
        };

    private static bool IsPhysicalTagColumn(string column) => column switch
    {
        "title" or "album_artist" or "artists_json" or "composers_json" or "conductors_json"
            or "album" or "genre" or "year" or "publisher"
            or "work_name" or "movement_name"
            or "tempo_bpm" or "mood_json" or "dj_scene_json"
            or "dj_vocals" or "dj_energy" => true,
        _ => false,
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
            ["rating"] = "rating_ts",
        };

    public string DbPath { get; }
    public string StateDbPath { get; }
    public string? MusicRoot { get; }

    public StagingDiffStore(string manifestDbPath, string? musicRoot = null)
    {
        DbPath = manifestDbPath;
        StateDbPath = DesktopStateStore.ResolveStateDbPath(manifestDbPath);
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
        string? dbDir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
        if (!string.IsNullOrWhiteSpace(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }
        DesktopStateStore.EnsureSchema(StateDbPath);
        if (File.Exists(DbPath))
        {
            using var conn = OpenConnection();
            EnsureOptionalTrackColumns(conn);
            DesktopStateStore.MigrateLegacyOperationalTablesIfPresent(DbPath, StateDbPath);
        }
    }

    public int EnqueueProposals(IEnumerable<TagDiffProposal> proposals, bool forceOverwriteUnlocked = false)
    {
        using var conn = OpenStateConnection();
        using var tx = TonewiseSqlite.BeginImmediateTransaction(conn);
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

    public IReadOnlyList<TagDiffProposal> GetPendingProposals(int limit = 0)
    {
        using var conn = OpenStateReadOnlyConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = limit > 0
            ? """
              SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                     confidence, source_tag, status, batch_id, created_at_ms
              FROM staging_diff_queue
              WHERE status IN ('Pending', 'Approved')
              ORDER BY CASE WHEN status = 'Approved' THEN 0 ELSE 1 END, confidence DESC, created_at_ms DESC
              LIMIT $lim;
              """
            : """
              SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                     confidence, source_tag, status, batch_id, created_at_ms
              FROM staging_diff_queue
              WHERE status IN ('Pending', 'Approved')
              ORDER BY CASE WHEN status = 'Approved' THEN 0 ELSE 1 END, confidence DESC, created_at_ms DESC;
              """;
        if (limit > 0)
        {
            cmd.Parameters.AddWithValue("$lim", limit);
        }

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

    public (int ApprovedCount, int PendingCount) GetPendingProposalCounts()
    {
        using var conn = OpenStateReadOnlyConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status = 'Approved' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'Pending' THEN 1 ELSE 0 END), 0)
            FROM staging_diff_queue;
            """;
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        return (0, 0);
    }

    public IReadOnlyList<string> GetCommitCandidateTrackUids(
        double autoApproveThreshold = 0.80,
        int maxProposals = 500)
    {
        using var conn = OpenStateReadOnlyConnection();
        using var cmd = conn.CreateCommand();
        int limit = maxProposals > 0 ? maxProposals : 100_000;
        cmd.CommandText = """
            SELECT DISTINCT track_uid
            FROM (
                SELECT track_uid
                FROM staging_diff_queue
                WHERE status = 'Approved' OR (status = 'Pending' AND confidence >= $thresh)
                ORDER BY CASE WHEN status = 'Approved' THEN 0 ELSE 1 END, confidence DESC, created_at_ms DESC
                LIMIT $lim
            );
            """;
        cmd.Parameters.AddWithValue("$thresh", autoApproveThreshold);
        cmd.Parameters.AddWithValue("$lim", limit);
        var uids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            uids.Add(reader.GetString(0));
        }
        return uids;
    }

    public void SetProposalApproval(string proposalId, bool approved)
    {
        using var conn = OpenStateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE staging_diff_queue SET status = $st WHERE proposal_id = $id;";
        cmd.Parameters.AddWithValue("$st", approved ? ProposalStatus.Approved : ProposalStatus.Rejected);
        cmd.Parameters.AddWithValue("$id", proposalId);
        cmd.ExecuteNonQuery();
    }

    public int ApproveAllHighConfidence(double minConfidence = 0.80)
    {
        using var conn = OpenStateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE staging_diff_queue
            SET status = 'Approved'
            WHERE status = 'Pending' AND confidence >= $thresh;
            """;
        cmd.Parameters.AddWithValue("$thresh", minConfidence);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes Pending and Rejected proposals only; explicitly Approved proposals are preserved.
    /// </summary>
    public int ClearPendingProposals()
    {
        using var conn = OpenStateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM staging_diff_queue WHERE status IN ('Pending', 'Rejected');";
        return cmd.ExecuteNonQuery();
    }

    public int CountPendingAtOrAbove(double minConfidence)
    {
        using var conn = OpenStateReadOnlyConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM staging_diff_queue WHERE status = 'Pending' AND confidence >= $thresh;";
        cmd.Parameters.AddWithValue("$thresh", minConfidence);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CurationCommitResult CommitApprovedBatch(
        string? customBatchId = null,
        double autoApproveThreshold = 0.80,
        int maxProposals = 500,
        int maxPhysicalFiles = 25)
    {
        string batchId = string.IsNullOrWhiteSpace(customBatchId)
            ? $"batch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : customBatchId;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var stateConn = OpenStateConnection();
        var targets = new List<TagDiffProposal>();
        using (var selectCmd = stateConn.CreateCommand())
        {
            int limit = maxProposals > 0 ? maxProposals : 100_000;
            selectCmd.CommandText = """
                SELECT proposal_id, track_uid, field_name, original_value, suggested_value,
                       confidence, source_tag, status, batch_id, created_at_ms
                FROM staging_diff_queue
                WHERE status = 'Approved' OR (status = 'Pending' AND confidence >= $thresh)
                ORDER BY CASE WHEN status = 'Approved' THEN 0 ELSE 1 END, confidence DESC, created_at_ms DESC
                LIMIT $lim;
                """;
            selectCmd.Parameters.AddWithValue("$thresh", autoApproveThreshold);
            selectCmd.Parameters.AddWithValue("$lim", limit);
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

        using var conn = OpenConnection();
        EnsureOptionalTrackColumns(conn);

        var batchTargets = new List<TagDiffProposal>(targets.Count);
        var updatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        int physicalLimit = maxPhysicalFiles > 0 ? maxPhysicalFiles : int.MaxValue;
        var tracksBeyondPhysicalLimit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in targets)
        {
            string column = ResolveColumnName(item.FieldName);
            if (IsPhysicalTagColumn(column))
            {
                if (tracksBeyondPhysicalLimit.Contains(item.TrackUid))
                {
                    continue;
                }
                if (!updatesByTrack.TryGetValue(item.TrackUid, out var trackUpdates))
                {
                    if (updatesByTrack.Count >= physicalLimit)
                    {
                        tracksBeyondPhysicalLimit.Add(item.TrackUid);
                        continue;
                    }
                    trackUpdates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    updatesByTrack[item.TrackUid] = trackUpdates;
                }
                trackUpdates[column] = NormalizePersonColumnForWrite(column, item.SuggestedValue);
            }
            batchTargets.Add(item);
        }

        if (batchTargets.Count == 0)
        {
            return new CurationCommitResult(batchId, 0);
        }

        var (physicalResults, physicalFailures) = WriteBackPhysicalFilesOutsideTx(conn, updatesByTrack, physicalLimit);

        int committed = 0;
        var committedItems = new List<TagDiffProposal>(batchTargets.Count);
        var failedPhysicalItems = new List<TagDiffProposal>();
        using (var tx = TonewiseSqlite.BeginImmediateTransaction(conn))
        {
            foreach (var item in batchTargets)
            {
                string column = ResolveColumnName(item.FieldName);
                if (IsPhysicalTagColumn(column) && physicalFailures.ContainsKey(item.TrackUid))
                {
                    failedPhysicalItems.Add(item);
                    continue;
                }
                ApplyTrackColumnValue(conn, tx, item.TrackUid, column, item.SuggestedValue, nowMs);
                committedItems.Add(item);
                committed++;
            }

            ApplyPhysicalFingerprintsInTx(conn, tx, physicalResults);
            tx.Commit();
        }

        using (var stateTx = TonewiseSqlite.BeginImmediateTransaction(stateConn))
        {
            foreach (var item in committedItems)
            {
                using var markCmd = stateConn.CreateCommand();
                markCmd.Transaction = stateTx;
                markCmd.CommandText = "UPDATE staging_diff_queue SET status = $st, batch_id = $bid WHERE proposal_id = $id;";
                markCmd.Parameters.AddWithValue("$st", ProposalStatus.Committed);
                markCmd.Parameters.AddWithValue("$bid", batchId);
                markCmd.Parameters.AddWithValue("$id", item.ProposalId);
                markCmd.ExecuteNonQuery();
            }

            foreach (var failedItem in failedPhysicalItems)
            {
                if (failedItem.Status != ProposalStatus.Approved)
                {
                    using var keepApprovedCmd = stateConn.CreateCommand();
                    keepApprovedCmd.Transaction = stateTx;
                    keepApprovedCmd.CommandText = "UPDATE staging_diff_queue SET status = $st, batch_id = NULL WHERE proposal_id = $id;";
                    keepApprovedCmd.Parameters.AddWithValue("$st", ProposalStatus.Approved);
                    keepApprovedCmd.Parameters.AddWithValue("$id", failedItem.ProposalId);
                    keepApprovedCmd.ExecuteNonQuery();
                }
            }

            if (committed > 0)
            {
                using var batchCmd = stateConn.CreateCommand();
                batchCmd.Transaction = stateTx;
                batchCmd.CommandText = """
                    INSERT INTO curation_batches (batch_id, committed_at_ms, item_count, rolled_back)
                    VALUES ($bid, $ts, $cnt, 0);
                    """;
                batchCmd.Parameters.AddWithValue("$bid", batchId);
                batchCmd.Parameters.AddWithValue("$ts", nowMs);
                batchCmd.Parameters.AddWithValue("$cnt", committed);
                batchCmd.ExecuteNonQuery();
            }
            stateTx.Commit();
        }

        var committedUids = committedItems.Select(t => t.TrackUid).Distinct(StringComparer.Ordinal).ToList();
        return new CurationCommitResult(
            batchId,
            committed,
            physicalResults.Count,
            committedUids,
            physicalFailures.Count > 0 ? physicalFailures.Values.ToList() : null);
    }

    public CurationCommitResult ApplyDirectCuratedEdits(
        IEnumerable<string> trackUids,
        IReadOnlyDictionary<string, string?> fieldUpdates)
    {
        var uids = trackUids
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string batchId = $"batch-lexicon-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        if (uids.Count == 0 || fieldUpdates.Count == 0)
        {
            return new CurationCommitResult(batchId, 0);
        }

        var normalizedUpdates = new List<(string Column, string? Value)>();
        foreach (var (key, val) in fieldUpdates)
        {
            string col = ResolveColumnName(key);
            normalizedUpdates.Add((col, NormalizePersonColumnForWrite(col, val)));
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenConnection();
        EnsureOptionalTrackColumns(conn);

        var fileUpdatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (string uid in uids)
        {
            foreach (var (col, val) in normalizedUpdates)
            {
                if (IsPhysicalTagColumn(col))
                {
                    if (!fileUpdatesByTrack.TryGetValue(uid, out var trackUpdates))
                    {
                        trackUpdates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        fileUpdatesByTrack[uid] = trackUpdates;
                    }
                    trackUpdates[col] = val;
                }
            }
        }

        var (physicalResults, physicalFailures) = WriteBackPhysicalFilesOutsideTx(conn, fileUpdatesByTrack);

        int committed = 0;
        var appliedAudit = new List<(string TrackUid, string Column, string? OrigVal, string NewVal)>();
        using (var tx = TonewiseSqlite.BeginImmediateTransaction(conn))
        {
            foreach (string uid in uids)
            {
                foreach (var (col, val) in normalizedUpdates)
                {
                    if (IsPhysicalTagColumn(col) && physicalFailures.ContainsKey(uid))
                    {
                        continue;
                    }

                    string? origVal = null;
                    using (var origCmd = conn.CreateCommand())
                    {
                        origCmd.Transaction = tx;
                        origCmd.CommandText = $"SELECT {col} FROM tracks WHERE track_uid = $uid;";
                        origCmd.Parameters.AddWithValue("$uid", uid);
                        object? rawOrig = origCmd.ExecuteScalar();
                        if (rawOrig is not null && rawOrig != DBNull.Value)
                        {
                            origVal = Convert.ToString(rawOrig, System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }

                    ApplyTrackColumnValue(conn, tx, uid, col, val, nowMs);
                    appliedAudit.Add((uid, col, origVal, val ?? ""));
                    committed++;
                }
            }

            ApplyPhysicalFingerprintsInTx(conn, tx, physicalResults);
            tx.Commit();
        }

        if (committed > 0)
        {
            using var stateConn = OpenStateConnection();
            using var stateTx = TonewiseSqlite.BeginImmediateTransaction(stateConn);
            foreach (var (uid, col, origVal, newVal) in appliedAudit)
            {
                using var qCmd = stateConn.CreateCommand();
                qCmd.Transaction = stateTx;
                qCmd.CommandText = """
                    INSERT INTO staging_diff_queue (
                        proposal_id, track_uid, field_name, original_value, suggested_value,
                        confidence, source_tag, status, batch_id, created_at_ms
                    ) VALUES ($id, $uid, $field, $orig, $sugg, 1.0, 'Lexicon-T', 'Committed', $bid, $ts)
                    ON CONFLICT(proposal_id) DO UPDATE SET
                        original_value = excluded.original_value,
                        suggested_value = excluded.suggested_value,
                        status = 'Committed',
                        batch_id = excluded.batch_id,
                        created_at_ms = excluded.created_at_ms;
                    """;
                qCmd.Parameters.AddWithValue("$id", $"lexicon:{batchId}:{uid}:{col}");
                qCmd.Parameters.AddWithValue("$uid", uid);
                qCmd.Parameters.AddWithValue("$field", col);
                qCmd.Parameters.AddWithValue("$orig", (object?)origVal ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$sugg", newVal);
                qCmd.Parameters.AddWithValue("$bid", batchId);
                qCmd.Parameters.AddWithValue("$ts", nowMs);
                qCmd.ExecuteNonQuery();
            }

            using var batchCmd = stateConn.CreateCommand();
            batchCmd.Transaction = stateTx;
            batchCmd.CommandText = """
                INSERT INTO curation_batches (batch_id, committed_at_ms, item_count, rolled_back)
                VALUES ($bid, $ts, $cnt, 0);
                """;
            batchCmd.Parameters.AddWithValue("$bid", batchId);
            batchCmd.Parameters.AddWithValue("$ts", nowMs);
            batchCmd.Parameters.AddWithValue("$cnt", committed);
            batchCmd.ExecuteNonQuery();
            stateTx.Commit();
        }

        var committedUids = appliedAudit.Select(a => a.TrackUid).Distinct(StringComparer.Ordinal).ToList();
        return new CurationCommitResult(
            batchId,
            committed,
            physicalResults.Count,
            committedUids,
            physicalFailures.Count > 0 ? physicalFailures.Values.ToList() : null);
    }

    public int RollbackBatch(string batchId) => RollbackBatchDetailed(batchId).RolledBackCount;

    public RollbackBatchResult RollbackBatchDetailed(string batchId)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var stateConn = OpenStateConnection();

        using (var checkCmd = stateConn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT rolled_back FROM curation_batches WHERE batch_id = $bid;";
            checkCmd.Parameters.AddWithValue("$bid", batchId);
            object? res = checkCmd.ExecuteScalar();
            if (res is null || Convert.ToInt64(res) != 0)
            {
                return new RollbackBatchResult(0, [], new Dictionary<string, Dictionary<string, string?>>());
            }
        }

        var items = new List<(string ProposalId, string TrackUid, string FieldName, string? OriginalValue, string? SuggestedValue)>();
        using (var listCmd = stateConn.CreateCommand())
        {
            listCmd.CommandText = """
                SELECT proposal_id, track_uid, field_name, original_value, suggested_value
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
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)
                ));
            }
        }

        using var conn = OpenConnection();
        EnsureOptionalTrackColumns(conn);

        var rollbackUpdatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string column = ResolveColumnName(item.FieldName);
            if (IsPhysicalTagColumn(column))
            {
                if (!rollbackUpdatesByTrack.TryGetValue(item.TrackUid, out var trackUpdates))
                {
                    trackUpdates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    rollbackUpdatesByTrack[item.TrackUid] = trackUpdates;
                }
                trackUpdates[column] = item.OriginalValue;
            }
        }

        var (physicalResults, physicalFailures) = WriteBackPhysicalFilesOutsideTx(conn, rollbackUpdatesByTrack);

        var rolledBackItems = new List<(string ProposalId, string TrackUid, string FieldName, string? OriginalValue, string? SuggestedValue)>();
        var forwardUpdatesByTrack = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        int skippedCount = 0;
        using (var tx = TonewiseSqlite.BeginImmediateTransaction(conn))
        {
            foreach (var item in items)
            {
                string column = ResolveColumnName(item.FieldName);
                if (IsPhysicalTagColumn(column) && physicalFailures.ContainsKey(item.TrackUid))
                {
                    skippedCount++;
                    continue;
                }
                ApplyTrackColumnValue(conn, tx, item.TrackUid, column, item.OriginalValue, nowMs);
                rolledBackItems.Add(item);
                if (!forwardUpdatesByTrack.TryGetValue(item.TrackUid, out var fwd))
                {
                    fwd = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    forwardUpdatesByTrack[item.TrackUid] = fwd;
                }
                fwd[column] = item.SuggestedValue;
            }

            ApplyPhysicalFingerprintsInTx(conn, tx, physicalResults);
            tx.Commit();
        }

        using (var stateTx = TonewiseSqlite.BeginImmediateTransaction(stateConn))
        {
            foreach (var item in rolledBackItems)
            {
                using var updCmd = stateConn.CreateCommand();
                updCmd.Transaction = stateTx;
                updCmd.CommandText = "UPDATE staging_diff_queue SET status = $st WHERE proposal_id = $id;";
                updCmd.Parameters.AddWithValue("$st", ProposalStatus.RolledBack);
                updCmd.Parameters.AddWithValue("$id", item.ProposalId);
                updCmd.ExecuteNonQuery();
            }

            if (skippedCount == 0)
            {
                using var markBatchCmd = stateConn.CreateCommand();
                markBatchCmd.Transaction = stateTx;
                markBatchCmd.CommandText = "UPDATE curation_batches SET rolled_back = 1 WHERE batch_id = $bid;";
                markBatchCmd.Parameters.AddWithValue("$bid", batchId);
                markBatchCmd.ExecuteNonQuery();
            }
            stateTx.Commit();
        }

        var affectedUids = rolledBackItems.Select(x => x.TrackUid).Distinct(StringComparer.Ordinal).ToList();
        return new RollbackBatchResult(rolledBackItems.Count, affectedUids, forwardUpdatesByTrack);
    }

    private (List<(string TrackUid, AudioFrameFingerprint Fingerprint, long MtimeMs)> Succeeded, Dictionary<string, string> FailedByTrack) WriteBackPhysicalFilesOutsideTx(
        SqliteConnection conn,
        Dictionary<string, Dictionary<string, string?>> updatesByTrack,
        int maxPhysicalFiles = int.MaxValue)
    {
        var results = new List<(string TrackUid, AudioFrameFingerprint Fingerprint, long MtimeMs)>();
        var failedByTrack = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(MusicRoot) || updatesByTrack.Count == 0 || maxPhysicalFiles <= 0)
            return (results, failedByTrack);

        string rootFull = Path.GetFullPath(MusicRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // Batch-fetch rel_path for target track UIDs up front to avoid per-track SQLite roundtrips.
        var relPathByUid = new Dictionary<string, string>(updatesByTrack.Count, StringComparer.Ordinal);
        var uidList = updatesByTrack.Keys.ToList();
        const int batchSize = 200;
        for (int offset = 0; offset < uidList.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, uidList.Count - offset);
            using var pathCmd = conn.CreateCommand();
            var paramNames = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string pName = $"$u{i}";
                paramNames.Add(pName);
                pathCmd.Parameters.AddWithValue(pName, uidList[offset + i]);
            }
            pathCmd.CommandText = $"SELECT track_uid, rel_path FROM tracks WHERE track_uid IN ({string.Join(",", paramNames)});";
            using var reader = pathCmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    string rel = reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(rel))
                    {
                        relPathByUid[reader.GetString(0)] = rel;
                    }
                }
            }
        }

        var workItems = new List<(string TrackUid, string RelPath, string FullPath, Dictionary<string, string?> Updates)>(updatesByTrack.Count);
        foreach (var (uid, updates) in updatesByTrack)
        {
            if (workItems.Count >= maxPhysicalFiles)
            {
                failedByTrack[uid] = "超出单批物理写回上限，已顺延至下一批次";
                continue;
            }
            if (!relPathByUid.TryGetValue(uid, out string? relPath)) continue;
            string fullPath = Path.GetFullPath(Path.Combine(MusicRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                failedByTrack[uid] = $"拒绝越界路径物理写回: {relPath}";
                continue;
            }
            if (!File.Exists(fullPath))
                continue;
            workItems.Add((uid, relPath, fullPath, updates));
        }

        if (workItems.Count == 1)
        {
            var w = workItems[0];
            try
            {
                var fp = TonewiseCoreClient.WriteAudioTags(w.FullPath, w.Updates);
                long mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(w.FullPath)).ToUnixTimeMilliseconds();
                results.Add((w.TrackUid, fp, mtimeMs));
            }
            catch (Exception ex)
            {
                failedByTrack[w.TrackUid] = $"{w.RelPath}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Physical tag writeback failed for {w.RelPath}: {ex.Message}");
            }
        }
        else if (workItems.Count > 1)
        {
            var bagOk = new ConcurrentBag<(string TrackUid, AudioFrameFingerprint Fingerprint, long MtimeMs)>();
            var bagErr = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            Parallel.ForEach(
                workItems,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                w =>
                {
                    try
                    {
                        var fp = TonewiseCoreClient.WriteAudioTags(w.FullPath, w.Updates);
                        long mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(w.FullPath)).ToUnixTimeMilliseconds();
                        bagOk.Add((w.TrackUid, fp, mtimeMs));
                    }
                    catch (Exception ex)
                    {
                        bagErr[w.TrackUid] = $"{w.RelPath}: {ex.Message}";
                        System.Diagnostics.Debug.WriteLine($"Physical tag writeback failed for {w.RelPath}: {ex.Message}");
                    }
                });
            results.AddRange(bagOk);
            foreach (var kv in bagErr)
            {
                failedByTrack[kv.Key] = kv.Value;
            }
        }
        return (results, failedByTrack);
    }

    private static void ApplyPhysicalFingerprintsInTx(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<(string TrackUid, AudioFrameFingerprint Fingerprint, long MtimeMs)> physicalResults)
    {
        foreach (var (uid, fp, mtimeMs) in physicalResults)
        {
            byte[] newHeaderHash = Convert.FromHexString(fp.HeaderBlake3Hex);
            using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = """
                UPDATE tracks
                SET header_size = $hsize,
                    header_blake3 = $hhash,
                    file_size = $fsize,
                    mtime_ms = $mtime
                WHERE track_uid = $uid;
                """;
            syncCmd.Parameters.AddWithValue("$hsize", (long)fp.HeaderSize);
            syncCmd.Parameters.AddWithValue("$hhash", newHeaderHash);
            syncCmd.Parameters.AddWithValue("$fsize", (long)fp.FileSize);
            syncCmd.Parameters.AddWithValue("$mtime", mtimeMs);
            syncCmd.Parameters.AddWithValue("$uid", uid);
            syncCmd.ExecuteNonQuery();
        }
    }

    private static string? NormalizePersonColumnForWrite(string column, string? rawValue)
    {
        if (rawValue is null) return null;
        if (column is "album_artist" or "artists_json" or "composers_json" or "conductors_json")
        {
            object coerced = CoerceColumnValue(column, rawValue);
            return coerced == DBNull.Value
                ? null
                : Convert.ToString(coerced, System.Globalization.CultureInfo.InvariantCulture);
        }
        return rawValue;
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
        if (column == "year")
        {
            return int.TryParse(rawValue.Trim(), out int y) ? y : DBNull.Value;
        }
        if (column == "rating")
        {
            return int.TryParse(rawValue.Trim(), out int r) ? Math.Clamp(r, 0, 5) : 0;
        }
        if (column == "tempo_bpm")
        {
            return double.TryParse(rawValue.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bpm) && double.IsFinite(bpm) && bpm > 0
                ? bpm
                : DBNull.Value;
        }
        if (column == "tempo_bucket")
        {
            return rawValue.Trim().ToLowerInvariant() switch
            {
                "very slow" or "veryslow" => "Very Slow",
                "slow" => "Slow",
                "moderate" or "medium" => "Moderate",
                "fast" or "upbeat" => "Fast",
                "very fast" or "veryfast" => "Very Fast",
                _ => DBNull.Value,
            };
        }
        if (column == "dj_vocals")
        {
            return rawValue.Trim().ToLowerInvariant() switch
            {
                "vocal" => "vocal",
                "instrumental" => "instrumental",
                _ => "unknown",
            };
        }
        if (column == "dj_energy")
        {
            return rawValue.Trim().ToLowerInvariant() switch
            {
                "low" => "low",
                "medium" or "moderate" => "medium",
                "high" => "high",
                _ => DBNull.Value,
            };
        }
        if (column == "album_artist")
        {
            string normalized = ArtistEntitySplitter.NormalizeAlbumArtist(rawValue);
            return string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : normalized;
        }
        if (column == "artists_json" || column == "composers_json" || column == "conductors_json")
        {
            bool composerMode = column == "composers_json";
            string trimmed = rawValue.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<string>>(trimmed) ?? [];
                    return JsonSerializer.Serialize(ArtistEntitySplitter.NormalizeEntityArray(items, composerMode));
                }
                catch
                {
                    return trimmed;
                }
            }
            var normalized = composerMode
                ? ArtistEntitySplitter.SplitComposers(trimmed)
                : ArtistEntitySplitter.SplitPerformers(trimmed);
            return JsonSerializer.Serialize(normalized);
        }
        if (column.EndsWith("_json", StringComparison.Ordinal))
        {
            string trimmed = rawValue.Trim();
            if (trimmed.StartsWith('[')) return trimmed;
            var parts = trimmed.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return JsonSerializer.Serialize(parts);
        }
        return string.IsNullOrWhiteSpace(rawValue) ? DBNull.Value : rawValue.Trim();
    }

    private static void EnsureOptionalTrackColumns(SqliteConnection conn) =>
        ManifestSchemaProvider.EnsureSchemaV2(conn);

    private SqliteConnection OpenConnection() => TonewiseSqlite.OpenReadWrite(DbPath);
    private SqliteConnection OpenStateConnection() => TonewiseSqlite.OpenReadWrite(StateDbPath);
    private SqliteConnection OpenStateReadOnlyConnection() => TonewiseSqlite.OpenReadOnly(StateDbPath);
}
