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
        var existingEvents = ReadJsonlEvents(cloudJsonlPath);
        var seenSet = existingEvents
            .Select(e => $"{e.TrackUid}@{e.PlayedAtUnixSec}")
            .ToHashSet(StringComparer.Ordinal);

        using var conn = OpenReadWrite(manifestDbPath);
        var trackByKey = BuildTrackLookupByNormalizedKey(conn, artistAliasMap);
        var newlyAddedByTrack = new Dictionary<string, int>(StringComparer.Ordinal);
        var newLines = new List<string>();
        int matchedTotal = 0;

        foreach (var ct in cloudTracks)
        {
            string key = NormalizeKey(ct.Artist, ct.Title, artistAliasMap);
            if (!trackByKey.TryGetValue(key, out string? uid)) continue;

            string stampKey = $"{uid}@{ct.PlayedAtUnixSec}";
            if (!seenSet.Add(stampKey)) continue;

            matchedTotal++;
            newlyAddedByTrack[uid] = newlyAddedByTrack.TryGetValue(uid, out int c) ? c + 1 : 1;

            var ev = new LedgerPlaybackEvent(
                TrackUid: uid,
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

        if (newlyAddedByTrack.Count > 0)
        {
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
        var trackByKey = BuildTrackLookupByNormalizedKey(conn, artistAliasMap);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int updated = 0;

        using var tx = conn.BeginTransaction();
        foreach (var loved in lovedTracks)
        {
            string key = NormalizeKey(loved.Artist, loved.Title, artistAliasMap);
            if (!trackByKey.TryGetValue(key, out string? uid)) continue;

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

    public static int MergeIncomingHistoryEvents(string manifestDbPath, IEnumerable<JsonElement> events)
    {
        if (!File.Exists(manifestDbPath)) return 0;
        new StagingDiffStore(manifestDbPath).EnsureSchema();

        using var conn = OpenReadWrite(manifestDbPath);
        using var tx = conn.BeginTransaction();
        int merged = 0;

        foreach (var ev in events)
        {
            if (!ev.TryGetProperty("track_uid", out var uidProp)) continue;
            string? uid = uidProp.GetString();
            if (string.IsNullOrWhiteSpace(uid)) continue;

            long utcTsMs = ev.TryGetProperty("utc_ts_ms", out var tsProp) && tsProp.TryGetInt64(out long tsVal)
                ? tsVal
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE tracks
                SET last_played_ts = CASE WHEN last_played_ts < $ts THEN $ts ELSE last_played_ts END
                WHERE track_uid = $uid;
                """;
            cmd.Parameters.AddWithValue("$ts", utcTsMs);
            cmd.Parameters.AddWithValue("$uid", uid);
            merged += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return merged;
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

    private static Dictionary<string, string> BuildTrackLookupByNormalizedKey(
        SqliteConnection conn,
        IReadOnlyDictionary<string, string>? artistAliasMap)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT track_uid, title, artists_json FROM tracks;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string title = reader.GetString(1);
            string artistsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
            List<string>? artists;
            try { artists = JsonSerializer.Deserialize<List<string>>(artistsJson); }
            catch { artists = null; }

            if (artists is null || artists.Count == 0) continue;

            string primaryKey = NormalizeKey(artists[0], title, artistAliasMap);
            map.TryAdd(primaryKey, uid);

            if (artists.Count > 1)
            {
                string joinedKey = NormalizeKey(string.Join(" & ", artists), title, artistAliasMap);
                map.TryAdd(joinedKey, uid);
            }
        }
        return map;
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
        string lower = raw.Trim().ToLowerInvariant();
        string stripped = NonWordRegex().Replace(lower, " ");
        return SpaceRegex().Replace(stripped, " ").Trim();
    }

    public static int MarkEventsScrobbled(string tonewiseDir, IEnumerable<LedgerPlaybackEvent> scrobbledItems)
    {
        if (!Directory.Exists(tonewiseDir)) return 0;
        var targetKeys = scrobbledItems
            .Select(e => $"{e.TrackUid}@{e.PlayedAtUnixSec}@{e.DeviceId}")
            .ToHashSet(StringComparer.Ordinal);
        if (targetKeys.Count == 0) return 0;

        int updated = 0;
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
        return updated;
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
}

public sealed record LastFmAccountConfig(
    string ApiKey,
    string ApiSecret,
    string Username,
    string SessionKey,
    long LastPullUnixSec = 0
)
{
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
    int PushedScrobbles
);

/// <summary>
/// Live Last.fm Web API v2.0 HTTP client supporting `auth.getMobileSession`,
/// `user.getRecentTracks`, `user.getLovedTracks`, and batched `track.scrobble` uplink.
/// </summary>
public sealed class LastFmWebApiClient
{
    private const string ApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private readonly HttpClient _http;

    public LastFmWebApiClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("session").GetProperty("key").GetString()
               ?? throw new InvalidOperationException("Last.fm mobile session key missing");
    }

    public async Task<IReadOnlyList<CloudRecentTrack>> FetchRecentTracksAsync(
        string apiKey,
        string username,
        long fromUnixSec = 0,
        int limit = 200,
        CancellationToken ct = default)
    {
        string url =
            $"{ApiBaseUrl}?method=user.getrecenttracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&limit={Math.Clamp(limit, 1, 200)}&format=json"
            + (fromUnixSec > 0 ? $"&from={fromUnixSec}" : "");

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return [];
        string json = await resp.Content.ReadAsStringAsync(ct);
        return ParseRecentTracksJson(json);
    }

    public async Task<IReadOnlyList<CloudLovedTrack>> FetchLovedTracksAsync(
        string apiKey,
        string username,
        int limit = 200,
        CancellationToken ct = default)
    {
        string url =
            $"{ApiBaseUrl}?method=user.getlovedtracks&user={Uri.EscapeDataString(username)}&api_key={Uri.EscapeDataString(apiKey)}&limit={Math.Clamp(limit, 1, 200)}&format=json";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return [];
        string json = await resp.Content.ReadAsStringAsync(ct);
        return ParseLovedTracksJson(json);
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
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
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

    public async Task<LastFmFullSyncSummary> RunFullBidirectionalSyncAsync(
        string manifestDbPath,
        string tonewiseDir,
        LastFmAccountConfig config,
        CancellationToken ct = default)
    {
        var recent = await FetchRecentTracksAsync(config.ApiKey, config.Username, config.LastPullUnixSec, ct: ct);
        int pulledCount = LastFmLedgerService.IngestPulledCloudPlays(manifestDbPath, tonewiseDir, recent);

        var loved = await FetchLovedTracksAsync(config.ApiKey, config.Username, ct: ct);
        int lovedCount = LastFmLedgerService.SyncLovedTracks(manifestDbPath, loved);

        long nowUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prep = LastFmLedgerService.PrepareScrobbleBatches(
            manifestDbPath, tonewiseDir, nowUnixSec, alreadyOnCloud: recent
        );

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

        long maxPulledTs = recent.Count > 0 ? recent.Max(r => r.PlayedAtUnixSec) + 1 : config.LastPullUnixSec;
        LastFmAccountConfig.Save(tonewiseDir, config with { LastPullUnixSec = Math.Max(config.LastPullUnixSec, maxPulledTs) });

        return new LastFmFullSyncSummary(
            PulledCloudPlays: pulledCount,
            LovedTracksUpdated: lovedCount,
            DeduplicatedCount: prep.DeduplicatedCount,
            ExpiredArchivedCount: prep.ExpiredCount,
            PushedScrobbles: pushedTotal
        );
    }

    private static IReadOnlyList<CloudRecentTrack> ParseRecentTracksJson(string json)
    {
        var list = new List<CloudRecentTrack>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recenttracks", out var rt)
                || !rt.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
                return list;

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
        return list;
    }

    private static IReadOnlyList<CloudLovedTrack> ParseLovedTracksJson(string json)
    {
        var list = new List<CloudLovedTrack>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lovedtracks", out var lt)
                || !lt.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
                return list;

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
        return list;
    }
}
