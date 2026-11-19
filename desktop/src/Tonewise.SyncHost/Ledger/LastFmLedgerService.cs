using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost.Curation;

namespace Tonewise.SyncHost.Ledger;

public sealed record CloudRecentTrack(
    string Artist,
    string Title,
    string Album,
    long PlayedAtUnixSec
);

public sealed record CloudLovedTrack(
    string Artist,
    string Title,
    long LovedAtUnixSec
);

public sealed record CloudTopTrack(
    string Artist,
    string Title,
    int PlayCount
);

public sealed record LedgerPlaybackEvent(
    string TrackUid,
    string Artist,
    string Title,
    string Album,
    long PlayedAtUnixSec,
    string DeviceId,
    bool ScrobbledToLastFm = false,
    bool ExpiredUnpushed = false
);

public sealed record LastFmScrobbleBatch(IReadOnlyList<LedgerPlaybackEvent> Items);

public sealed record LedgerScrobblePrepareResult(
    IReadOnlyList<LastFmScrobbleBatch> Batches,
    int DeduplicatedCount,
    int ExpiredCount
);

public sealed record IncomingHistoryMergeResult(
    int MergedTracks,
    Dictionary<string, int> EffectiveCounts,
    List<string> LovedUids
);

public sealed record PendingLastFmLoveOp(
    string TrackUid,
    string Artist,
    string Title,
    bool IsLoved,
    long UpdatedAtUnixSec
);

public static class PlaybackScrobbleEvaluator
{
    public static bool ShouldCommitPlay(int durationMs, double accumulatedPlayMs, bool completedNaturally)
    {
        if (accumulatedPlayMs <= 0) return false;
        if (completedNaturally)
        {
            if (durationMs <= 0)
            {
                return accumulatedPlayMs >= 3_000.0;
            }
            double naturalMinMs = Math.Min(5_000.0, Math.Max(1_000.0, durationMs * 0.25));
            return accumulatedPlayMs >= naturalMinMs;
        }

        return durationMs >= 30_000 && accumulatedPlayMs >= Math.Min(durationMs * 0.5, 240_000.0);
    }

    public static bool IsEligibleForLastFmScrobble(int durationMs, double accumulatedPlayMs, bool completedNaturally)
    {
        if (durationMs < 30_000) return false;
        double requiredMs = Math.Min(durationMs * 0.5, 240_000.0);
        return accumulatedPlayMs >= requiredMs;
    }

    public static string ExtractPrimaryArtist(string? artistsDisplay)
    {
        if (string.IsNullOrWhiteSpace(artistsDisplay)) return "Unknown Artist";
        foreach (string part in artistsDisplay.Split([',', ';', '、', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part)) return part;
        }
        return artistsDisplay.Trim();
    }
}

/// <summary>
/// Implements the Asymmetric High-Water-Mark Last.fm Ledger:
/// `effective_play_count = max(legacy_count, lastfm_pulled_count) + local_unsynced_expired_count`,
/// with bidirectional loved-track sync, 14-day scrobble expiry quarantine, and ±120s cloud dedup.
/// </summary>
public static partial class LastFmLedgerService
{
    public const long LastFmWindowSeconds = 14L * 86_400L;
    public const long CloudDedupToleranceSeconds = 120L;
    public const int MaxBatchSize = 50;

    private static readonly Lock HistoryFileLock = new();
    private static readonly Lock LoveOutboxLock = new();

    public static int ComputeEffectivePlayCount(
        int legacyCount,
        int lastFmPulledCount,
        int localUnsyncedExpiredCount)
    {
        return Math.Max(Math.Max(legacyCount, 0), Math.Max(lastFmPulledCount, 0))
               + Math.Max(localUnsyncedExpiredCount, 0);
    }

    public static string NormalizeKey(
        string artist,
        string title,
        IReadOnlyDictionary<string, string>? artistAliasMap = null)
    {
        string rawArtist = artist.Trim();
        if (artistAliasMap is not null)
        {
            foreach (var kv in artistAliasMap)
            {
                if (string.Equals(kv.Key.Trim(), rawArtist, StringComparison.OrdinalIgnoreCase))
                {
                    rawArtist = kv.Value.Trim();
                    break;
                }
            }
        }

        string normArtist = NormalizeToken(rawArtist);
        string normTitle = NormalizeToken(title);
        return $"{normArtist}||{normTitle}";
    }

    public static int IngestPulledCloudPlays(
        string manifestDbPath,
        string tonewiseDir,
        IEnumerable<CloudRecentTrack> cloudTracks,
        IReadOnlyDictionary<string, string>? artistAliasMap = null)
    {
        if (!File.Exists(manifestDbPath)) return 0;
        Directory.CreateDirectory(tonewiseDir);
        new StagingDiffStore(manifestDbPath).EnsureSchema();

        string cloudJsonlPath = Path.Combine(tonewiseDir, "history.lastfm.jsonl");
        List<string> newLines;
        Dictionary<string, int> newlyAddedByTrack;
        int matchedTotal;

        lock (HistoryFileLock)
        {
            var existingEvents = ReadJsonlEvents(cloudJsonlPath);
            var seenSet = existingEvents
                .Select(e => $"{e.TrackUid}@{e.PlayedAtUnixSec}")
                .ToHashSet(StringComparer.Ordinal);

            using var conn = OpenReadWrite(manifestDbPath);
            var index = BuildTrackLookupIndex(conn, artistAliasMap);
            newlyAddedByTrack = new Dictionary<string, int>(StringComparer.Ordinal);
            newLines = new List<string>();
            matchedTotal = 0;

            foreach (var ct in cloudTracks)
            {
                if (!TryResolveTrackUids(ct.Artist, ct.Title, artistAliasMap, index, out var uids) || uids.Count == 0) continue;

                string primaryUid = uids[0];
                string stampKey = $"{primaryUid}@{ct.PlayedAtUnixSec}";
                if (!seenSet.Add(stampKey)) continue;

                matchedTotal++;
                foreach (string uid in uids)
                {
                    newlyAddedByTrack[uid] = newlyAddedByTrack.TryGetValue(uid, out int c) ? c + 1 : 1;
                }

                var ev = new LedgerPlaybackEvent(
                    TrackUid: primaryUid,
                    Artist: ct.Artist,
                    Title: ct.Title,
                    Album: ct.Album,
                    PlayedAtUnixSec: ct.PlayedAtUnixSec,
                    DeviceId: "lastfm-cloud",
                    ScrobbledToLastFm: true,
                    ExpiredUnpushed: false
                );
                newLines.Add(JsonSerializer.Serialize(ev));
            }

            if (newLines.Count > 0)
            {
                File.AppendAllLines(cloudJsonlPath, newLines, new UTF8Encoding(false));
            }
        }

        if (newlyAddedByTrack.Count > 0)
        {
            using var conn = OpenReadWrite(manifestDbPath);
            using var tx = conn.BeginTransaction();
            foreach (var (uid, delta) in newlyAddedByTrack)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tracks
                    SET lastfm_pulled_count = lastfm_pulled_count + $delta,
                        play_count_total = MAX(legacy_play_count, lastfm_pulled_count + $delta) + local_unsynced_expired_count
                    WHERE track_uid = $uid;
                    """;
                cmd.Parameters.AddWithValue("$delta", delta);
                cmd.Parameters.AddWithValue("$uid", uid);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        return matchedTotal;
    }

    public static int SyncLovedTracks(
        string manifestDbPath,
        IEnumerable<CloudLovedTrack> lovedTracks,
        IReadOnlyDictionary<string, string>? artistAliasMap = null)
    {
        if (!File.Exists(manifestDbPath)) return 0;
        new StagingDiffStore(manifestDbPath).EnsureSchema();

        using var conn = OpenReadWrite(manifestDbPath);
        var index = BuildTrackLookupIndex(conn, artistAliasMap);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int updated = 0;

        using var tx = conn.BeginTransaction();
        foreach (var loved in lovedTracks)
        {
            if (!TryResolveTrackUids(loved.Artist, loved.Title, artistAliasMap, index, out var uids) || uids.Count == 0) continue;

            foreach (string uid in uids)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tracks
                    SET rating = 5,
                        rating_ts = CASE WHEN rating_ts < $ts THEN $ts ELSE rating_ts END
                    WHERE track_uid = $uid AND rating < 5;
                    """;
                cmd.Parameters.AddWithValue("$ts", nowMs);
                cmd.Parameters.AddWithValue("$uid", uid);
                updated += cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return updated;
    }

    public static int SyncTopTrackPlayCounts(
        string manifestDbPath,
        IEnumerable<CloudTopTrack> topTracks,
        IReadOnlyDictionary<string, string>? artistAliasMap = null)
    {
        if (!File.Exists(manifestDbPath)) return 0;
        new StagingDiffStore(manifestDbPath).EnsureSchema();

        using var conn = OpenReadWrite(manifestDbPath);
        var index = BuildTrackLookupIndex(conn, artistAliasMap);
        var maxByUid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var top in topTracks)
        {
            if (top.PlayCount <= 0) continue;
            if (!TryResolveTrackUids(top.Artist, top.Title, artistAliasMap, index, out var uids) || uids.Count == 0) continue;
            foreach (string uid in uids)
            {
                maxByUid[uid] = Math.Max(maxByUid.GetValueOrDefault(uid, 0), top.PlayCount);
            }
        }

        if (maxByUid.Count == 0) return 0;

        int updated = 0;
        using var tx = conn.BeginTransaction();
        foreach (var (uid, cloudCount) in maxByUid)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE tracks
                SET lastfm_pulled_count = MAX(lastfm_pulled_count, $cnt),
                    play_count_total = MAX(legacy_play_count, MAX(lastfm_pulled_count, $cnt)) + local_unsynced_expired_count
                WHERE track_uid = $uid AND lastfm_pulled_count < $cnt;
                """;
            cmd.Parameters.AddWithValue("$cnt", cloudCount);
            cmd.Parameters.AddWithValue("$uid", uid);
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }

    public static LedgerScrobblePrepareResult PrepareScrobbleBatches(
        string manifestDbPath,
        string tonewiseDir,
        long nowUnixSec,
        IReadOnlyDictionary<string, string>? artistAliasMap = null,
        IReadOnlyList<CloudRecentTrack>? alreadyOnCloud = null)
    {
        Directory.CreateDirectory(tonewiseDir);
        bool hasDb = File.Exists(manifestDbPath);
        if (hasDb) new StagingDiffStore(manifestDbPath).EnsureSchema();

        var cloudKeys = (alreadyOnCloud ?? [])
            .Select(c => (Key: NormalizeKey(c.Artist, c.Title, artistAliasMap), Ts: c.PlayedAtUnixSec))
            .ToList();

        var trackMetaByUid = hasDb
            ? BuildTrackMetaByUid(manifestDbPath)
            : new Dictionary<string, (string Artist, string Title, string Album)>();

        var pendingForPush = new List<LedgerPlaybackEvent>();
        var expiredByTrackUid = new Dictionary<string, int>(StringComparer.Ordinal);
        int deduplicatedCount = 0;
        int expiredCount = 0;

        lock (HistoryFileLock)
        {
            string[] jsonlFiles = Directory.GetFiles(tonewiseDir, "history*.jsonl");
            foreach (string filePath in jsonlFiles)
            {
                var events = ReadJsonlEvents(filePath);
                if (events.Count == 0) continue;

                bool modifiedFile = false;
                for (int i = 0; i < events.Count; i++)
                {
                    var ev = events[i];
                    if ((string.IsNullOrWhiteSpace(ev.Artist) || string.IsNullOrWhiteSpace(ev.Title))
                        && trackMetaByUid.TryGetValue(ev.TrackUid, out var meta))
                    {
                        ev = ev with { Artist = meta.Artist, Title = meta.Title, Album = meta.Album };
                        events[i] = ev;
                    }
                    if (ev.ScrobbledToLastFm || ev.ExpiredUnpushed) continue;

                    long ageSec = nowUnixSec - ev.PlayedAtUnixSec;
                    if (ageSec > LastFmWindowSeconds)
                    {
                        events[i] = ev with { ExpiredUnpushed = true };
                        modifiedFile = true;
                        expiredCount++;
                        if (!string.IsNullOrWhiteSpace(ev.TrackUid))
                        {
                            expiredByTrackUid[ev.TrackUid] = expiredByTrackUid.TryGetValue(ev.TrackUid, out int c) ? c + 1 : 1;
                        }
                        continue;
                    }

                    string localKey = NormalizeKey(ev.Artist, ev.Title, artistAliasMap);
                    bool matchesCloud = cloudKeys.Any(ck =>
                        ck.Key == localKey && Math.Abs(ck.Ts - ev.PlayedAtUnixSec) <= CloudDedupToleranceSeconds);

                    if (matchesCloud)
                    {
                        events[i] = ev with { ScrobbledToLastFm = true };
                        modifiedFile = true;
                        deduplicatedCount++;
                    }
                    else
                    {
                        pendingForPush.Add(ev);
                    }
                }

                if (modifiedFile)
                {
                    var rewritten = events.Select(e => JsonSerializer.Serialize(e));
                    File.WriteAllLines(filePath, rewritten, new UTF8Encoding(false));
                }
            }
        }

        if (hasDb && expiredByTrackUid.Count > 0)
        {
            using var conn = OpenReadWrite(manifestDbPath);
            using var tx = conn.BeginTransaction();
            foreach (var (uid, delta) in expiredByTrackUid)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tracks
                    SET local_unsynced_expired_count = local_unsynced_expired_count + $delta,
                        play_count_total = MAX(legacy_play_count, lastfm_pulled_count) + (local_unsynced_expired_count + $delta)
                    WHERE track_uid = $uid;
                    """;
                cmd.Parameters.AddWithValue("$delta", delta);
                cmd.Parameters.AddWithValue("$uid", uid);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var batches = pendingForPush
            .OrderBy(e => e.PlayedAtUnixSec)
            .Chunk(MaxBatchSize)
            .Select(chunk => new LastFmScrobbleBatch(chunk.ToList()))
            .ToList();

        return new LedgerScrobblePrepareResult(batches, deduplicatedCount, expiredCount);
    }

    internal sealed class TrackUidResolver
    {
        private readonly Dictionary<string, string> _uidByKey;
        private readonly TrackLookupIndex _lookupIndex;

        private TrackUidResolver(Dictionary<string, string> uidByKey, TrackLookupIndex lookupIndex)
        {
            _uidByKey = uidByKey;
            _lookupIndex = lookupIndex;
        }

        public static TrackUidResolver Build(SqliteConnection conn)
        {
            var uidByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var qUids = conn.CreateCommand())
            {
                qUids.CommandText = "SELECT track_uid, lower(hex(audio_frame_blake3)) FROM tracks;";
                using var reader = qUids.ExecuteReader();
                while (reader.Read())
                {
                    string tUid = reader.GetString(0);
                    uidByKey[tUid] = tUid;
                    if (!reader.IsDBNull(1))
                    {
                        string hex = reader.GetString(1);
                        if (!string.IsNullOrWhiteSpace(hex))
                            uidByKey.TryAdd(hex, tUid);
                    }
                }
            }
            return new TrackUidResolver(uidByKey, BuildTrackLookupIndex(conn, null));
        }

        public string? ResolveFromJson(JsonElement obj)
        {
            if (obj.TryGetProperty("track_uid", out var uidProp) && uidProp.ValueKind == JsonValueKind.String)
            {
                string? rawUid = uidProp.GetString();
                if (!string.IsNullOrWhiteSpace(rawUid) && _uidByKey.TryGetValue(rawUid.Trim(), out var mapped))
                    return mapped;
            }
            if (obj.TryGetProperty("audio_frame_blake3_hex", out var b3Prop) && b3Prop.ValueKind == JsonValueKind.String)
            {
                string? rawB3 = b3Prop.GetString();
                if (!string.IsNullOrWhiteSpace(rawB3) && _uidByKey.TryGetValue(rawB3.Trim(), out var mapped))
                    return mapped;
            }
            if (obj.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
            {
                string title = titleProp.GetString() ?? "";
                string artist = obj.TryGetProperty("artist", out var artProp) && artProp.ValueKind == JsonValueKind.String
                    ? artProp.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(title)
                    && TryResolveTrackUids(artist, title, null, _lookupIndex, out var matchedUids)
                    && matchedUids.Count == 1)
                {
                    return matchedUids[0];
                }
            }
            return null;
        }
    }

    public static int MergeIncomingHistoryEvents(string manifestDbPath, IEnumerable<JsonElement> events) =>
        MergeIncomingHistoryEventsDetailed(manifestDbPath, events).MergedTracks;

    public static IncomingHistoryMergeResult MergeIncomingHistoryEventsDetailed(
        string manifestDbPath,
        IEnumerable<JsonElement> events,
        string? deviceId = null)
    {
        if (!File.Exists(manifestDbPath))
            return new IncomingHistoryMergeResult(0, new Dictionary<string, int>(StringComparer.Ordinal), []);

        new StagingDiffStore(manifestDbPath).EnsureSchema();

        using var conn = OpenReadWrite(manifestDbPath);
        EnsureLanSyncAckTable(conn);

        var resolver = TrackUidResolver.Build(conn);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var tx = conn.BeginTransaction();
        int merged = 0;

        foreach (var ev in events)
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;

            string? resolvedUid = resolver.ResolveFromJson(ev);
            if (string.IsNullOrWhiteSpace(resolvedUid)) continue;

            int rawDelta = 0;
            bool hasPlayDeltaProp = ev.TryGetProperty("play_count_delta", out var deltaProp)
                && deltaProp.ValueKind == JsonValueKind.Number
                && deltaProp.TryGetInt32(out rawDelta);
            int delta = hasPlayDeltaProp ? Math.Max(0, rawDelta) : 0;

            long lastPlayedMs = 0;
            if (ev.TryGetProperty("last_played_ts_ms", out var lptProp)
                && lptProp.ValueKind == JsonValueKind.Number
                && lptProp.TryGetInt64(out long lptVal))
            {
                lastPlayedMs = Math.Max(0L, lptVal);
            }
            else if (ev.TryGetProperty("utc_ts_ms", out var utcProp)
                && utcProp.ValueKind == JsonValueKind.Number
                && utcProp.TryGetInt64(out long utcVal))
            {
                lastPlayedMs = Math.Max(0L, utcVal);
            }
            else if (!hasPlayDeltaProp)
            {
                lastPlayedMs = nowMs;
            }

            if (delta > 0)
            {
                if (ev.TryGetProperty("event_id", out var evIdProp) && evIdProp.ValueKind == JsonValueKind.String)
                {
                    string? evId = evIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(evId))
                    {
                        string ackKey = string.IsNullOrWhiteSpace(deviceId)
                            ? evId.Trim()
                            : $"{deviceId.Trim()}::{evId.Trim()}";
                        using var ackCmd = conn.CreateCommand();
                        ackCmd.Transaction = tx;
                        ackCmd.CommandText = """
                            INSERT OR IGNORE INTO lan_sync_play_acks (event_key, track_uid, delta, applied_at_ms)
                            VALUES ($k, $uid, $d, $now);
                            """;
                        ackCmd.Parameters.AddWithValue("$k", ackKey);
                        ackCmd.Parameters.AddWithValue("$uid", resolvedUid);
                        ackCmd.Parameters.AddWithValue("$d", delta);
                        ackCmd.Parameters.AddWithValue("$now", nowMs);
                        if (ackCmd.ExecuteNonQuery() == 0)
                        {
                            continue;
                        }
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tracks
                    SET local_unsynced_expired_count = local_unsynced_expired_count + $delta,
                        play_count_total = MAX(legacy_play_count, lastfm_pulled_count) + (local_unsynced_expired_count + $delta),
                        last_played_ts = CASE WHEN $ts > 0 AND last_played_ts < $ts THEN $ts ELSE last_played_ts END
                    WHERE track_uid = $uid;
                    """;
                cmd.Parameters.AddWithValue("$delta", delta);
                cmd.Parameters.AddWithValue("$ts", lastPlayedMs);
                cmd.Parameters.AddWithValue("$uid", resolvedUid);
                merged += cmd.ExecuteNonQuery();
            }
            else if (lastPlayedMs > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE tracks
                    SET last_played_ts = CASE WHEN last_played_ts < $ts THEN $ts ELSE last_played_ts END
                    WHERE track_uid = $uid;
                    """;
                cmd.Parameters.AddWithValue("$ts", lastPlayedMs);
                cmd.Parameters.AddWithValue("$uid", resolvedUid);
                merged += cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        var (effectiveCounts, lovedUids) = ReadEffectiveCountsAndLoved(conn);
        return new IncomingHistoryMergeResult(merged, effectiveCounts, lovedUids);
    }

    public static (Dictionary<string, int> EffectiveCounts, List<string> LovedUids) ReadEffectiveCountsAndLoved(string manifestDbPath)
    {
        if (!File.Exists(manifestDbPath))
            return (new Dictionary<string, int>(StringComparer.Ordinal), []);

        new StagingDiffStore(manifestDbPath).EnsureSchema();
        using var conn = OpenReadWrite(manifestDbPath);
        return ReadEffectiveCountsAndLoved(conn);
    }

    private static (Dictionary<string, int> EffectiveCounts, List<string> LovedUids) ReadEffectiveCountsAndLoved(SqliteConnection conn)
    {
        var effectiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lovedUids = new List<string>();
        var lovedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_uid, lower(hex(audio_frame_blake3)), play_count_total, rating FROM tracks WHERE play_count_total > 0 OR rating >= 5;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string hex = reader.IsDBNull(1) ? "" : reader.GetString(1);
            int total = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            int rating = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);

            if (total > 0)
            {
                effectiveCounts[uid] = total;
                if (hex.Length == 64 && !string.Equals(uid, hex, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveCounts.TryAdd(hex, total);
                }
            }

            if (rating >= 5)
            {
                if (lovedSeen.Add(uid)) lovedUids.Add(uid);
                if (hex.Length == 64 && !string.Equals(uid, hex, StringComparison.OrdinalIgnoreCase) && lovedSeen.Add(hex))
                {
                    lovedUids.Add(hex);
                }
            }
        }

        return (effectiveCounts, lovedUids);
    }

    private static void EnsureLanSyncAckTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS lan_sync_play_acks (
                event_key TEXT PRIMARY KEY,
                track_uid TEXT NOT NULL,
                delta INTEGER NOT NULL,
                applied_at_ms INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static string BuildLastFmApiSignature(
        IReadOnlyDictionary<string, string> parameters,
        string apiSecret)
    {
        var sb = new StringBuilder();
        foreach (var kv in parameters
                     .Where(p => !string.Equals(p.Key, "format", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append(kv.Value);
        }
        sb.Append(apiSecret);

        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static Dictionary<string, (string Artist, string Title, string Album)> BuildTrackMetaByUid(string dbPath)
    {
        var map = new Dictionary<string, (string Artist, string Title, string Album)>(StringComparer.Ordinal);
        using var conn = OpenReadWrite(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_uid, title, album, artists_json FROM tracks;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string title = reader.GetString(1);
            string album = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string artistsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
            string primaryArtist = "";
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(artistsJson);
                if (arr is { Count: > 0 }) primaryArtist = arr[0];
            }
            catch { /* Ignore invalid JSON */ }
            map[uid] = (primaryArtist, title, album);
        }
        return map;
    }

    private sealed record TrackLookupIndex(
        Dictionary<string, List<string>> ByExactArtistTitle,
        Dictionary<string, List<string>> ByCoreArtistCoreTitle,
        Dictionary<string, List<string>> ByExactTitle,
        Dictionary<string, List<string>> ByCoreTitle
    );

    private static TrackLookupIndex BuildTrackLookupIndex(
        SqliteConnection conn,
        IReadOnlyDictionary<string, string>? artistAliasMap)
    {
        var exactMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var coreMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var exactTitleMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var coreTitleMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void AddIndex(Dictionary<string, List<string>> dict, string key, string uid)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<string>(2);
                dict[key] = list;
            }
            if (!list.Contains(uid)) list.Add(uid);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_uid, title, artists_json, album_artist FROM tracks;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string title = reader.GetString(1);
            string artistsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
            string albumArtist = reader.IsDBNull(3) ? "" : reader.GetString(3);

            string normTitle = NormalizeToken(title);
            string coreTitle = NormalizeCoreTitle(title);
            AddIndex(exactTitleMap, normTitle, uid);
            AddIndex(coreTitleMap, coreTitle, uid);

            // Also index post-colon work subtitle (e.g. "Schumann:Sicilienne" -> "Sicilienne")
            int colonIdx = title.IndexOf(':');
            string? postColonCore = colonIdx > 1 && colonIdx < title.Length - 2
                ? NormalizeCoreTitle(title[(colonIdx + 1)..])
                : null;
            if (!string.IsNullOrWhiteSpace(postColonCore) && postColonCore != coreTitle)
            {
                AddIndex(coreTitleMap, postColonCore, uid);
            }

            List<string>? artists;
            try { artists = JsonSerializer.Deserialize<List<string>>(artistsJson); }
            catch { artists = null; }
            artists ??= [];
            if (!string.IsNullOrWhiteSpace(albumArtist) && !artists.Contains(albumArtist, StringComparer.OrdinalIgnoreCase))
            {
                artists.Add(albumArtist);
            }

            foreach (string artist in artists)
            {
                if (string.IsNullOrWhiteSpace(artist)) continue;
                string exactKey = NormalizeKey(artist, title, artistAliasMap);
                AddIndex(exactMap, exactKey, uid);

                string coreArtist = NormalizeCoreArtist(artist, artistAliasMap);
                if (!string.IsNullOrWhiteSpace(coreArtist) && !string.IsNullOrWhiteSpace(coreTitle))
                {
                    AddIndex(coreMap, $"{coreArtist}||{coreTitle}", uid);
                    if (!string.IsNullOrWhiteSpace(postColonCore))
                    {
                        AddIndex(coreMap, $"{coreArtist}||{postColonCore}", uid);
                    }
                }
            }

            if (artists.Count > 1)
            {
                string joinedArtist = string.Join(" & ", artists);
                AddIndex(exactMap, NormalizeKey(joinedArtist, title, artistAliasMap), uid);
                string coreJoined = NormalizeCoreArtist(joinedArtist, artistAliasMap);
                if (!string.IsNullOrWhiteSpace(coreJoined) && !string.IsNullOrWhiteSpace(coreTitle))
                {
                    AddIndex(coreMap, $"{coreJoined}||{coreTitle}", uid);
                }
            }
        }
        return new TrackLookupIndex(exactMap, coreMap, exactTitleMap, coreTitleMap);
    }

    private static bool TryResolveTrackUids(
        string artist,
        string title,
        IReadOnlyDictionary<string, string>? artistAliasMap,
        TrackLookupIndex index,
        out IReadOnlyList<string> uids)
    {
        string key = NormalizeKey(artist, title, artistAliasMap);
        if (index.ByExactArtistTitle.TryGetValue(key, out var exactHits) && exactHits.Count > 0)
        {
            uids = exactHits;
            return true;
        }

        string coreArtist = NormalizeCoreArtist(artist, artistAliasMap);
        string coreTitle = NormalizeCoreTitle(title);
        if (!string.IsNullOrWhiteSpace(coreArtist) && !string.IsNullOrWhiteSpace(coreTitle)
            && index.ByCoreArtistCoreTitle.TryGetValue($"{coreArtist}||{coreTitle}", out var coreHits)
            && coreHits.Count > 0)
        {
            uids = coreHits;
            return true;
        }

        int colonIdx = title.IndexOf(':');
        if (colonIdx > 1 && colonIdx < title.Length - 2 && !string.IsNullOrWhiteSpace(coreArtist))
        {
            string postColon = NormalizeCoreTitle(title[(colonIdx + 1)..]);
            if (!string.IsNullOrWhiteSpace(postColon)
                && index.ByCoreArtistCoreTitle.TryGetValue($"{coreArtist}||{postColon}", out var colonHits)
                && colonHits.Count > 0)
            {
                uids = colonHits;
                return true;
            }
        }

        string normTitle = NormalizeToken(title);
        if (!string.IsNullOrEmpty(normTitle)
            && index.ByExactTitle.TryGetValue(normTitle, out var titleHits)
            && titleHits.Count is >= 1 and <= 3)
        {
            uids = titleHits;
            return true;
        }

        if (!string.IsNullOrEmpty(coreTitle)
            && index.ByCoreTitle.TryGetValue(coreTitle, out var coreTitleHits)
            && coreTitleHits.Count is >= 1 and <= 3)
        {
            uids = coreTitleHits;
            return true;
        }

        uids = [];
        return false;
    }

    private static List<LedgerPlaybackEvent> ReadJsonlEvents(string path)
    {
        if (!File.Exists(path)) return [];
        var list = new List<LedgerPlaybackEvent>();
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<LedgerPlaybackEvent>(line);
                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.TrackUid))
                {
                    list.Add(parsed);
                    continue;
                }

                // Tolerate Android/CLI history event shapes with utc_ts_ms + track_uid
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("track_uid", out var uidEl))
                {
                    string uid = uidEl.GetString() ?? "";
                    long tsSec = root.TryGetProperty("played_at_sec", out var ps) && ps.TryGetInt64(out long s1)
                        ? s1
                        : (root.TryGetProperty("utc_ts_ms", out var ms) && ms.TryGetInt64(out long m1) ? m1 / 1000L : 0L);
                    string artist = root.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "";
                    string title = root.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    string album = root.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";
                    string dev = root.TryGetProperty("device_id", out var dv) ? dv.GetString() ?? "unknown" : "unknown";
                    bool scrobbled = root.TryGetProperty("scrobbled_to_lastfm", out var sc) && sc.ValueKind == JsonValueKind.True;
                    bool expired = root.TryGetProperty("expired_unpushed", out var ex) && ex.ValueKind == JsonValueKind.True;
                    if (!string.IsNullOrWhiteSpace(uid) && tsSec > 0)
                    {
                        list.Add(new LedgerPlaybackEvent(uid, artist, title, album, tsSec, dev, scrobbled, expired));
                    }
                }
            }
            catch
            {
                // Skip malformed JSONL line
            }
        }
        return list;
    }

    private static string NormalizeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string simplified = ToSimplifiedChinese(raw.Trim());
        string lower = simplified.ToLowerInvariant();
        string stripped = NonWordRegex().Replace(lower, " ");
        return SpaceRegex().Replace(stripped, " ").Trim();
    }

    private static string NormalizeCoreTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string simplified = ToSimplifiedChinese(raw.Trim());
        string noParen = EditionParenRegex().Replace(simplified, " ");
        string noBracket = EditionBracketRegex().Replace(noParen, " ");
        string noDash = EditionDashRegex().Replace(noBracket, "");
        string result = NormalizeToken(noDash);
        return string.IsNullOrEmpty(result) ? NormalizeToken(simplified) : result;
    }

    private static string NormalizeCoreArtist(string raw, IReadOnlyDictionary<string, string>? artistAliasMap)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        if (artistAliasMap is not null)
        {
            foreach (var kv in artistAliasMap)
            {
                if (string.Equals(kv.Key.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = kv.Value.Trim();
                    break;
                }
            }
        }
        string noFeat = FeatRegex().Replace(trimmed, "");
        string result = NormalizeToken(noFeat);
        return string.IsNullOrEmpty(result) ? NormalizeToken(trimmed) : result;
    }

    private static string ToSimplifiedChinese(string input)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(input)) return input;
        const uint LcmapSimplifiedChinese = 0x02000000;
        try
        {
            var sb = new StringBuilder(input.Length);
            int written = LCMapStringEx("zh-CN", LcmapSimplifiedChinese, input, input.Length, sb, input.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return written > 0 ? sb.ToString(0, written) : input;
        }
        catch
        {
            return input;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        StringBuilder lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    public static int MarkEventsScrobbled(string tonewiseDir, IEnumerable<LedgerPlaybackEvent> scrobbledItems)
    {
        if (!Directory.Exists(tonewiseDir)) return 0;
        var targetKeys = scrobbledItems
            .Select(e => $"{e.TrackUid}@{e.PlayedAtUnixSec}@{e.DeviceId}")
            .ToHashSet(StringComparer.Ordinal);
        if (targetKeys.Count == 0) return 0;

        int updated = 0;
        lock (HistoryFileLock)
        {
            foreach (string filePath in Directory.GetFiles(tonewiseDir, "history*.jsonl"))
            {
                var events = ReadJsonlEvents(filePath);
                bool modified = false;
                for (int i = 0; i < events.Count; i++)
                {
                    var ev = events[i];
                    if (!ev.ScrobbledToLastFm && targetKeys.Contains($"{ev.TrackUid}@{ev.PlayedAtUnixSec}@{ev.DeviceId}"))
                    {
                        events[i] = ev with { ScrobbledToLastFm = true };
                        modified = true;
                        updated++;
                    }
                }
                if (modified)
                {
                    File.WriteAllLines(filePath, events.Select(e => JsonSerializer.Serialize(e)), new UTF8Encoding(false));
                }
            }
        }
        return updated;
    }

    public static void RecordLocalPlaybackEvent(
        string manifestDbPath,
        string tonewiseDir,
        string trackUid,
        string artist,
        string title,
        string album,
        long playedAtUnixSec,
        bool scrobbleEligible,
        string deviceId = "desktop")
    {
        if (string.IsNullOrWhiteSpace(tonewiseDir) || string.IsNullOrWhiteSpace(trackUid)) return;
        Directory.CreateDirectory(tonewiseDir);

        string cleanArtist = PlaybackScrobbleEvaluator.ExtractPrimaryArtist(artist);
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "Unknown Track" : title.Trim();
        string cleanAlbum = album?.Trim() ?? "";

        if (scrobbleEligible)
        {
            string desktopJsonl = Path.Combine(tonewiseDir, "history.desktop.jsonl");
            var ev = new LedgerPlaybackEvent(
                TrackUid: trackUid,
                Artist: cleanArtist,
                Title: cleanTitle,
                Album: cleanAlbum,
                PlayedAtUnixSec: playedAtUnixSec,
                DeviceId: string.IsNullOrWhiteSpace(deviceId) ? "desktop" : deviceId,
                ScrobbledToLastFm: false,
                ExpiredUnpushed: false
            );
            string line = JsonSerializer.Serialize(ev);
            lock (HistoryFileLock)
            {
                File.AppendAllLines(desktopJsonl, [line], new UTF8Encoding(false));
            }
        }

        if (!string.IsNullOrWhiteSpace(manifestDbPath) && File.Exists(manifestDbPath))
        {
            long playedTsMs = playedAtUnixSec * 1000L;
            using var conn = OpenReadWrite(manifestDbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE tracks
                SET play_count_total = play_count_total + 1,
                    last_played_ts = CASE WHEN last_played_ts < $ts THEN $ts ELSE last_played_ts END
                WHERE track_uid = $uid;
                """;
            cmd.Parameters.AddWithValue("$ts", playedTsMs);
            cmd.Parameters.AddWithValue("$uid", trackUid);
            cmd.ExecuteNonQuery();
        }
    }

    private static string GetLovedOutboxPath(string tonewiseDir) =>
        Path.Combine(tonewiseDir, "lastfm_loved_outbox.json");

    public static void EnqueueLoveChange(
        string tonewiseDir,
        string trackUid,
        string artist,
        string title,
        bool isLoved,
        long? updatedAtUnixSec = null)
    {
        if (string.IsNullOrWhiteSpace(tonewiseDir)) return;
        Directory.CreateDirectory(tonewiseDir);

        string cleanArtist = PlaybackScrobbleEvaluator.ExtractPrimaryArtist(artist);
        string cleanTitle = title?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(cleanTitle)) return;

        string targetKey = NormalizeKey(cleanArtist, cleanTitle);
        long ts = updatedAtUnixSec ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var incoming = new PendingLastFmLoveOp(trackUid, cleanArtist, cleanTitle, isLoved, ts);

        lock (LoveOutboxLock)
        {
            var ops = ReadPendingLoveOpsUnlocked(tonewiseDir);
            ops.RemoveAll(op =>
                (!string.IsNullOrWhiteSpace(trackUid) && string.Equals(op.TrackUid, trackUid, StringComparison.Ordinal))
                || string.Equals(NormalizeKey(op.Artist, op.Title), targetKey, StringComparison.Ordinal));
            ops.Add(incoming);
            WritePendingLoveOpsUnlocked(tonewiseDir, ops);
        }
    }

    public static IReadOnlyList<PendingLastFmLoveOp> ReadPendingLoveOps(string tonewiseDir)
    {
        if (string.IsNullOrWhiteSpace(tonewiseDir)) return [];
        lock (LoveOutboxLock)
        {
            return ReadPendingLoveOpsUnlocked(tonewiseDir);
        }
    }

    public static void RemoveCompletedLoveOps(string tonewiseDir, IEnumerable<PendingLastFmLoveOp> completed)
    {
        if (string.IsNullOrWhiteSpace(tonewiseDir)) return;
        var completedSet = completed.ToList();
        if (completedSet.Count == 0) return;

        var completedKeys = completedSet
            .Select(op => $"{NormalizeKey(op.Artist, op.Title)}|{op.IsLoved}|{op.UpdatedAtUnixSec}")
            .ToHashSet(StringComparer.Ordinal);

        lock (LoveOutboxLock)
        {
            var ops = ReadPendingLoveOpsUnlocked(tonewiseDir);
            ops.RemoveAll(op =>
                completedKeys.Contains($"{NormalizeKey(op.Artist, op.Title)}|{op.IsLoved}|{op.UpdatedAtUnixSec}"));
            WritePendingLoveOpsUnlocked(tonewiseDir, ops);
        }
    }

    public static int EnqueueAllLocalFiveStarAsLoved(string manifestDbPath, string tonewiseDir)
    {
        if (string.IsNullOrWhiteSpace(manifestDbPath) || !File.Exists(manifestDbPath)) return 0;
        Directory.CreateDirectory(tonewiseDir);

        var items = new List<(string Uid, string Artist, string Title)>();
        using (var conn = OpenReadWrite(manifestDbPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT track_uid, artists_json, COALESCE(album_artist, ''), title FROM tracks WHERE rating >= 5;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string uid = reader.GetString(0);
                string artistsJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                string albumArtist = reader.IsDBNull(2) ? "" : reader.GetString(2);
                string title = reader.IsDBNull(3) ? "" : reader.GetString(3);

                string artist = albumArtist;
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(artistsJson);
                    string? first = arr?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    if (!string.IsNullOrWhiteSpace(first)) artist = first;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    items.Add((uid, PlaybackScrobbleEvaluator.ExtractPrimaryArtist(artist), title));
                }
            }
        }

        if (items.Count == 0) return 0;
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (LoveOutboxLock)
        {
            var ops = ReadPendingLoveOpsUnlocked(tonewiseDir);
            foreach (var (uid, artist, title) in items)
            {
                string key = NormalizeKey(artist, title);
                bool hasPendingUnlove = ops.Any(op =>
                    !op.IsLoved
                    && (string.Equals(op.TrackUid, uid, StringComparison.Ordinal)
                        || string.Equals(NormalizeKey(op.Artist, op.Title), key, StringComparison.Ordinal)));
                if (hasPendingUnlove) continue;

                ops.RemoveAll(op =>
                    op.IsLoved
                    && (string.Equals(op.TrackUid, uid, StringComparison.Ordinal)
                        || string.Equals(NormalizeKey(op.Artist, op.Title), key, StringComparison.Ordinal)));
                ops.Add(new PendingLastFmLoveOp(uid, artist, title, IsLoved: true, nowSec));
            }
            WritePendingLoveOpsUnlocked(tonewiseDir, ops);
        }
        return items.Count;
    }

    private static List<PendingLastFmLoveOp> ReadPendingLoveOpsUnlocked(string tonewiseDir)
    {
        string path = GetLovedOutboxPath(tonewiseDir);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PendingLastFmLoveOp>>(File.ReadAllText(path, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WritePendingLoveOpsUnlocked(string tonewiseDir, List<PendingLastFmLoveOp> ops)
    {
        string path = GetLovedOutboxPath(tonewiseDir);
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..6];
        string json = JsonSerializer.Serialize(ops, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static SqliteConnection OpenReadWrite(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    [GeneratedRegex(@"[\p{P}\p{S}]")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"\((?:[^)]*(?:remaster|feat\.?|ft\.?|live|version|edit|mix|mono|stereo|deluxe|bonus|acoustic|soundtrack|ost|single|中文版|国语|粤语)[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex EditionParenRegex();

    [GeneratedRegex(@"\[(?:[^\]]*(?:remaster|feat\.?|ft\.?|live|version|edit|mix|mono|stereo|deluxe|bonus|acoustic)[^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex EditionBracketRegex();

    [GeneratedRegex(@"\s+-\s+(?:.*(?:remaster|version|edit|mix|live|mono|stereo|soundtrack|from\s+|single|中文版|国语版|粤语版).*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionDashRegex();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();
}

public sealed record LastFmAccountConfig(
    string ApiKey,
    string ApiSecret,
    string Username,
    string SessionKey,
    long LastPullUnixSec = 0
)
{
    public bool HasWriteSession =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(SessionKey);

    public static LastFmAccountConfig? Load(string tonewiseDir)
    {
        string path = Path.Combine(tonewiseDir, "lastfm_config.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LastFmAccountConfig>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string tonewiseDir, LastFmAccountConfig config)
    {
        Directory.CreateDirectory(tonewiseDir);
        string path = Path.Combine(tonewiseDir, "lastfm_config.json");
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

public sealed record LastFmFullSyncSummary(
    int PulledCloudPlays,
    int LovedTracksUpdated,
    int DeduplicatedCount,
    int ExpiredArchivedCount,
    int PushedScrobbles,
    int TopPlayCountsUpdated = 0,
    int PushedLoved = 0,
    int PushedUnloved = 0
);

/// <summary>
/// Live Last.fm Web API v2.0 HTTP client supporting `auth.getMobileSession`,
/// `auth.getToken` / `auth.getSession` browser OAuth, `user.getRecentTracks`,
/// `user.getLovedTracks`, `track.updateNowPlaying`, `track.love`/`track.unlove`,
/// and batched `track.scrobble` uplink.
/// </summary>
public sealed class LastFmWebApiClient
{
    private const string ApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http;

    public LastFmWebApiClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttpClient;
    }

    private static void ThrowIfLastFmJsonError(JsonDocument doc, string operation)
    {
        if (!doc.RootElement.TryGetProperty("error", out var errProp)) return;
        int code = errProp.ValueKind == JsonValueKind.Number
            ? errProp.GetInt32()
            : (int.TryParse(errProp.GetString(), out int parsed) ? parsed : -1);
        string message = doc.RootElement.TryGetProperty("message", out var msgEl)
            ? (msgEl.GetString() ?? "Unknown error")
            : "Unknown error";

        string hint = code switch
        {
            4 => "Last.fm 认证失败：请检查用户名/密码或改用浏览器授权",
            9 => "Last.fm 会话已失效 (错误 9)：请重新进行浏览器授权登录",
            10 => "Last.fm API Key 无效 (错误 10)",
            13 => "Last.fm API 签名无效 (错误 13)：请核对 32 位 Shared Secret",
            14 => "尚未在浏览器中点击「Yes, allow access」完成授权 (错误 14)",
            _ => $"Last.fm {operation} 失败 (错误 {code})：{message}",
        };
        throw new InvalidOperationException(hint);
    }

    public async Task<(string Token, string AuthorizeUrl)> RequestAuthTokenAsync(
        string apiKey,
        string apiSecret,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = apiKey.Trim(),
        };
        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret.Trim());
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, "auth.getToken");
        resp.EnsureSuccessStatusCode();

        string token = doc.RootElement.GetProperty("token").GetString()
                       ?? throw new InvalidOperationException("Last.fm auth.getToken 未返回 token");
        string authorizeUrl =
            $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey.Trim())}&token={Uri.EscapeDataString(token)}";
        return (token, authorizeUrl);
    }

    public async Task<(string SessionKey, string Username)> ExchangeAuthTokenForSessionAsync(
        string apiKey,
        string apiSecret,
        string token,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["api_key"] = apiKey.Trim(),
            ["token"] = token.Trim(),
        };
        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret.Trim());
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, "auth.getSession");
        resp.EnsureSuccessStatusCode();

        var sessionEl = doc.RootElement.GetProperty("session");
        string sk = sessionEl.GetProperty("key").GetString()
                    ?? throw new InvalidOperationException("Last.fm auth.getSession 未返回 session key");
        string name = sessionEl.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
        return (sk, name);
    }

    public async Task<string> AuthenticateMobileSessionAsync(
        string apiKey,
        string apiSecret,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getMobileSession",
            ["api_key"] = apiKey,
            ["username"] = username,
            ["password"] = password,
        };
        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret);
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, "auth.getMobileSession");
        resp.EnsureSuccessStatusCode();

        return doc.RootElement.GetProperty("session").GetProperty("key").GetString()
               ?? throw new InvalidOperationException("Last.fm mobile session key missing");
    }

    public async Task UpdateNowPlayingAsync(
        string apiKey,
        string apiSecret,
        string sessionKey,
        string artist,
        string title,
        string? album = null,
        int durationSec = 0,
        CancellationToken ct = default)
    {
        string cleanArtist = PlaybackScrobbleEvaluator.ExtractPrimaryArtist(artist);
        if (string.IsNullOrWhiteSpace(title)) return;

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = apiKey,
            ["sk"] = sessionKey,
            ["artist"] = cleanArtist,
            ["track"] = title.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(album))
        {
            parameters["album"] = album.Trim();
        }
        if (durationSec > 0)
        {
            parameters["duration"] = durationSec.ToString();
        }
        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret);
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, "track.updateNowPlaying");
        resp.EnsureSuccessStatusCode();
    }

    public async Task PushTrackLoveStateAsync(
        string apiKey,
        string apiSecret,
        string sessionKey,
        string artist,
        string title,
        bool isLoved,
        CancellationToken ct = default)
    {
        string cleanArtist = PlaybackScrobbleEvaluator.ExtractPrimaryArtist(artist);
        if (string.IsNullOrWhiteSpace(title)) return;

        string method = isLoved ? "track.love" : "track.unlove";
        var parameters = new Dictionary<string, string>
        {
            ["method"] = method,
            ["api_key"] = apiKey,
            ["sk"] = sessionKey,
            ["artist"] = cleanArtist,
            ["track"] = title.Trim(),
        };
        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret);
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, method);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<T>> FetchPagedAsync<T>(
        Func<int, Task<(IReadOnlyList<T> Items, int TotalPages)>> fetchPage,
        int maxPagesCap,
        int maxConcurrency,
        CancellationToken ct)
    {
        var (firstItems, totalPages) = await fetchPage(1);
        int maxPage = Math.Min(Math.Max(1, totalPages), maxPagesCap);
        if (maxPage <= 1 || firstItems.Count == 0)
        {
            return firstItems;
        }

        var pageBuckets = new IReadOnlyList<T>[maxPage + 1];
        pageBuckets[1] = firstItems;

        using var sem = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>(maxPage - 1);
        for (int p = 2; p <= maxPage; p++)
        {
            int pageNum = p;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var (items, _) = await fetchPage(pageNum);
                    pageBuckets[pageNum] = items;
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        var combined = new List<T>(firstItems.Count * maxPage);
        for (int p = 1; p <= maxPage; p++)
        {
            if (pageBuckets[p] is { Count: > 0 } items)
            {
                combined.AddRange(items);
            }
        }
        return combined;
    }

    public async Task<IReadOnlyList<CloudRecentTrack>> FetchRecentTracksAsync(
        string apiKey,
        string username,
        long fromUnixSec = 0,
        int limit = 200,
        CancellationToken ct = default)
    {
        int perPage = Math.Clamp(limit, 1, 200);
        return await FetchPagedAsync(
            async page =>
            {
                string url =
                    $"{ApiBaseUrl}?method=user.getrecenttracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&limit={perPage}&page={page}&format=json"
                    + (fromUnixSec > 0 ? $"&from={fromUnixSec}" : "");

                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return ((IReadOnlyList<CloudRecentTrack>)[], 1);
                string json = await resp.Content.ReadAsStringAsync(ct);
                return ParseRecentTracksJson(json);
            },
            maxPagesCap: 15,
            maxConcurrency: 4,
            ct);
    }

    public async Task<IReadOnlyList<CloudLovedTrack>> FetchLovedTracksAsync(
        string apiKey,
        string username,
        int limit = 1000,
        CancellationToken ct = default)
    {
        int perPage = Math.Clamp(limit, 1, 1000);
        return await FetchPagedAsync(
            async page =>
            {
                string url =
                    $"{ApiBaseUrl}?method=user.getlovedtracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&limit={perPage}&page={page}&format=json";

                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return ((IReadOnlyList<CloudLovedTrack>)[], 1);
                string json = await resp.Content.ReadAsStringAsync(ct);
                return ParseLovedTracksJson(json);
            },
            maxPagesCap: 50,
            maxConcurrency: 4,
            ct);
    }

    public async Task<IReadOnlyList<CloudTopTrack>> FetchUserTopTracksAsync(
        string apiKey,
        string username,
        int limit = 1000,
        CancellationToken ct = default)
    {
        int perPage = Math.Clamp(limit, 1, 1000);
        return await FetchPagedAsync(
            async page =>
            {
                string url =
                    $"{ApiBaseUrl}?method=user.gettoptracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&period=overall&limit={perPage}&page={page}&format=json";

                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return ((IReadOnlyList<CloudTopTrack>)[], 1);
                string json = await resp.Content.ReadAsStringAsync(ct);
                return ParseTopTracksJson(json);
            },
            maxPagesCap: 100,
            maxConcurrency: 5,
            ct);
    }

    public async Task<int> PushScrobbleBatchAsync(
        string apiKey,
        string apiSecret,
        string sessionKey,
        LastFmScrobbleBatch batch,
        CancellationToken ct = default)
    {
        if (batch.Items.Count == 0) return 0;

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.scrobble",
            ["api_key"] = apiKey,
            ["sk"] = sessionKey,
        };

        for (int i = 0; i < batch.Items.Count; i++)
        {
            var item = batch.Items[i];
            parameters[$"artist[{i}]"] = item.Artist;
            parameters[$"track[{i}]"] = item.Title;
            parameters[$"timestamp[{i}]"] = item.PlayedAtUnixSec.ToString();
            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                parameters[$"album[{i}]"] = item.Album;
            }
        }

        parameters["api_sig"] = LastFmLedgerService.BuildLastFmApiSignature(parameters, apiSecret);
        parameters["format"] = "json";

        using var resp = await _http.PostAsync(ApiBaseUrl, new FormUrlEncodedContent(parameters), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ThrowIfLastFmJsonError(doc, "track.scrobble");
        resp.EnsureSuccessStatusCode();

        if (doc.RootElement.TryGetProperty("scrobbles", out var scr)
            && scr.TryGetProperty("@attr", out var attr)
            && attr.TryGetProperty("accepted", out var acc))
        {
            return acc.ValueKind == JsonValueKind.Number
                ? acc.GetInt32()
                : (int.TryParse(acc.GetString(), out int parsed) ? parsed : batch.Items.Count);
        }
        return batch.Items.Count;
    }

    public async Task<int> FlushPendingScrobblesAsync(
        string manifestDbPath,
        string tonewiseDir,
        LastFmAccountConfig config,
        CancellationToken ct = default)
    {
        if (!config.HasWriteSession) return 0;
        long nowUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prep = LastFmLedgerService.PrepareScrobbleBatches(manifestDbPath, tonewiseDir, nowUnixSec);
        int pushedTotal = 0;
        foreach (var batch in prep.Batches)
        {
            int accepted = await PushScrobbleBatchAsync(config.ApiKey, config.ApiSecret, config.SessionKey, batch, ct);
            if (accepted > 0)
            {
                LastFmLedgerService.MarkEventsScrobbled(tonewiseDir, batch.Items);
                pushedTotal += accepted;
            }
        }
        return pushedTotal;
    }

    public async Task<(int LovedPushed, int UnlovedPushed)> FlushPendingLoveOpsAsync(
        string tonewiseDir,
        LastFmAccountConfig config,
        CancellationToken ct = default)
    {
        if (!config.HasWriteSession) return (0, 0);
        var pending = LastFmLedgerService.ReadPendingLoveOps(tonewiseDir);
        if (pending.Count == 0) return (0, 0);

        var completed = new List<PendingLastFmLoveOp>(pending.Count);
        int lovedCount = 0;
        int unlovedCount = 0;
        try
        {
            foreach (var op in pending)
            {
                await PushTrackLoveStateAsync(
                    config.ApiKey,
                    config.ApiSecret,
                    config.SessionKey,
                    op.Artist,
                    op.Title,
                    op.IsLoved,
                    ct
                );
                completed.Add(op);
                if (op.IsLoved) lovedCount++;
                else unlovedCount++;
            }
        }
        finally
        {
            if (completed.Count > 0)
            {
                LastFmLedgerService.RemoveCompletedLoveOps(tonewiseDir, completed);
            }
        }

        return (lovedCount, unlovedCount);
    }

    public async Task<LastFmFullSyncSummary> RunFullBidirectionalSyncAsync(
        string manifestDbPath,
        string tonewiseDir,
        LastFmAccountConfig config,
        CancellationToken ct = default)
    {
        int pushedLoved = 0;
        int pushedUnloved = 0;
        if (config.HasWriteSession)
        {
            (pushedLoved, pushedUnloved) = await FlushPendingLoveOpsAsync(tonewiseDir, config, ct);
        }

        var topTracks = await FetchUserTopTracksAsync(config.ApiKey, config.Username, ct: ct);
        int topUpdated = LastFmLedgerService.SyncTopTrackPlayCounts(manifestDbPath, topTracks);

        long effectiveFromSec = config.LastPullUnixSec;
        var recent = await FetchRecentTracksAsync(config.ApiKey, config.Username, effectiveFromSec, ct: ct);
        if (effectiveFromSec > 0 && recent.Count == 0)
        {
            // Recover from a future or skewed watermark (e.g. testing clock ahead of Last.fm server time)
            recent = await FetchRecentTracksAsync(config.ApiKey, config.Username, fromUnixSec: 0, ct: ct);
        }
        int pulledCount = LastFmLedgerService.IngestPulledCloudPlays(manifestDbPath, tonewiseDir, recent);

        var loved = await FetchLovedTracksAsync(config.ApiKey, config.Username, ct: ct);
        int lovedCount = LastFmLedgerService.SyncLovedTracks(manifestDbPath, loved);

        long nowUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prep = LastFmLedgerService.PrepareScrobbleBatches(
            manifestDbPath, tonewiseDir, nowUnixSec, alreadyOnCloud: recent
        );

        int pushedTotal = 0;
        if (config.HasWriteSession)
        {
            foreach (var batch in prep.Batches)
            {
                int accepted = await PushScrobbleBatchAsync(config.ApiKey, config.ApiSecret, config.SessionKey, batch, ct);
                if (accepted > 0)
                {
                    LastFmLedgerService.MarkEventsScrobbled(tonewiseDir, batch.Items);
                    pushedTotal += accepted;
                }
            }
        }

        long maxPulledTs = recent.Count > 0 ? recent.Max(r => r.PlayedAtUnixSec) + 1 : config.LastPullUnixSec;
        LastFmAccountConfig.Save(tonewiseDir, config with { LastPullUnixSec = maxPulledTs });

        return new LastFmFullSyncSummary(
            PulledCloudPlays: pulledCount + topUpdated,
            LovedTracksUpdated: lovedCount,
            DeduplicatedCount: prep.DeduplicatedCount,
            ExpiredArchivedCount: prep.ExpiredCount,
            PushedScrobbles: pushedTotal,
            TopPlayCountsUpdated: topUpdated,
            PushedLoved: pushedLoved,
            PushedUnloved: pushedUnloved
        );
    }

    private static int ReadTotalPages(JsonElement container)
    {
        if (container.TryGetProperty("@attr", out var attr)
            && attr.TryGetProperty("totalPages", out var tp))
        {
            if (tp.ValueKind == JsonValueKind.Number && tp.TryGetInt32(out int num)) return Math.Max(1, num);
            if (int.TryParse(tp.GetString(), out int parsed)) return Math.Max(1, parsed);
        }
        return 1;
    }

    private static (IReadOnlyList<CloudRecentTrack> Items, int TotalPages) ParseRecentTracksJson(string json)
    {
        var list = new List<CloudRecentTrack>();
        int totalPages = 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recenttracks", out var rt))
                return (list, totalPages);
            totalPages = ReadTotalPages(rt);
            if (!rt.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
                return (list, totalPages);

            foreach (var tr in tracks.EnumerateArray())
            {
                if (tr.TryGetProperty("@attr", out var attr)
                    && attr.TryGetProperty("nowplaying", out var np)
                    && string.Equals(np.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!tr.TryGetProperty("date", out var dt) || !dt.TryGetProperty("uts", out var utsEl))
                    continue;
                if (!long.TryParse(utsEl.GetString(), out long uts)) continue;

                string artist = tr.TryGetProperty("artist", out var ar) && ar.TryGetProperty("#text", out var arText)
                    ? (arText.GetString() ?? "")
                    : "";
                string title = tr.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                string album = tr.TryGetProperty("album", out var al) && al.TryGetProperty("#text", out var alText)
                    ? (alText.GetString() ?? "")
                    : "";

                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                {
                    list.Add(new CloudRecentTrack(artist, title, album, uts));
                }
            }
        }
        catch { /* Return partial results on schema mismatch */ }
        return (list, totalPages);
    }

    private static (IReadOnlyList<CloudLovedTrack> Items, int TotalPages) ParseLovedTracksJson(string json)
    {
        var list = new List<CloudLovedTrack>();
        int totalPages = 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lovedtracks", out var lt))
                return (list, totalPages);
            totalPages = ReadTotalPages(lt);
            if (!lt.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
                return (list, totalPages);

            foreach (var tr in tracks.EnumerateArray())
            {
                string artist = tr.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var arName)
                    ? (arName.GetString() ?? "")
                    : "";
                string title = tr.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                long uts = tr.TryGetProperty("date", out var dt)
                           && dt.TryGetProperty("uts", out var utsEl)
                           && long.TryParse(utsEl.GetString(), out long parsedTs)
                    ? parsedTs
                    : 0L;

                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                {
                    list.Add(new CloudLovedTrack(artist, title, uts));
                }
            }
        }
        catch { /* Return partial results safely */ }
        return (list, totalPages);
    }

    private static (IReadOnlyList<CloudTopTrack> Items, int TotalPages) ParseTopTracksJson(string json)
    {
        var list = new List<CloudTopTrack>();
        int totalPages = 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("toptracks", out var tt))
                return (list, totalPages);
            totalPages = ReadTotalPages(tt);
            if (!tt.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
                return (list, totalPages);

            foreach (var tr in tracks.EnumerateArray())
            {
                string artist = tr.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var arName)
                    ? (arName.GetString() ?? "")
                    : "";
                string title = tr.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                int playCount = 0;
                if (tr.TryGetProperty("playcount", out var pc))
                {
                    if (pc.ValueKind == JsonValueKind.Number) pc.TryGetInt32(out playCount);
                    else int.TryParse(pc.GetString(), out playCount);
                }

                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title) && playCount > 0)
                {
                    list.Add(new CloudTopTrack(artist, title, playCount));
                }
            }
        }
        catch { /* Return partial results safely */ }
        return (list, totalPages);
    }
}
