using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost;

public sealed record PairedDeviceRecord(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("token_hash_sha256")] string TokenHashSha256,
    [property: JsonPropertyName("paired_at_ms")] long PairedAtMs,
    [property: JsonPropertyName("last_sync_ms")] long LastSyncMs
);

public sealed record SyncOutboxItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target_key")] string TargetKey,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("track_uids")] IReadOnlyList<string> TrackUids,
    [property: JsonPropertyName("created_at_ms")] long CreatedAtMs
);

public sealed record ResolvedSyncTrack(
    [property: JsonPropertyName("track_uid")] string TrackUid,
    [property: JsonPropertyName("rel_path")] string RelPath,
    [property: JsonPropertyName("target_rel_path")] string TargetRelPath,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("album_artist")] string AlbumArtist,
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("mtime_ms")] long MtimeMs,
    [property: JsonPropertyName("has_cover")] bool HasCover,
    [property: JsonPropertyName("source_outbox_ids")] IReadOnlyList<string> SourceOutboxIds
);

public sealed record ResolvedSyncPlaylist(
    [property: JsonPropertyName("outbox_id")] string OutboxId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rel_paths")] IReadOnlyList<string> RelPaths,
    [property: JsonPropertyName("track_uids")] IReadOnlyList<string> TrackUids
);

public sealed record ResolvedOutboxPayload(
    [property: JsonPropertyName("host_name")] string HostName,
    [property: JsonPropertyName("generated_at_ms")] long GeneratedAtMs,
    [property: JsonPropertyName("items")] IReadOnlyList<SyncOutboxItem> Items,
    [property: JsonPropertyName("tracks")] IReadOnlyList<ResolvedSyncTrack> Tracks,
    [property: JsonPropertyName("playlists")] IReadOnlyList<ResolvedSyncPlaylist> Playlists,
    [property: JsonPropertyName("total_bytes")] long TotalBytes
);

public sealed record CatalogEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("track_count")] int TrackCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes
);

public sealed record OutboxSummary(int ItemCount, int TrackCount, long TotalBytes);

public sealed class SyncOutboxAndPairingStore
{
    private static readonly string[] CoverCandidateNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png"
    ];

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    private readonly string _musicRoot;
    private readonly string _tonewiseDir;
    private readonly string _devicesPath;
    private readonly string _outboxPath;
    private readonly Lock _gate = new();
    private string _pairingCode;

    public SyncOutboxAndPairingStore(string musicRoot)
    {
        _musicRoot = musicRoot;
        _tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        _devicesPath = Path.Combine(_tonewiseDir, "paired_devices.json");
        _outboxPath = Path.Combine(_tonewiseDir, "sync_outbox.json");
        _pairingCode = GenerateSixDigitCode();
    }

    public string CurrentPairingCode
    {
        get
        {
            lock (_gate)
            {
                return _pairingCode;
            }
        }
    }

    public string RegeneratePairingCode()
    {
        lock (_gate)
        {
            _pairingCode = GenerateSixDigitCode();
            return _pairingCode;
        }
    }

    public bool TryPair(
        string? code,
        string? deviceId,
        string? deviceName,
        out string? issuedToken,
        out PairedDeviceRecord? device)
    {
        issuedToken = null;
        device = null;

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(deviceId))
            return false;

        string submitted = code.Trim();
        string cleanDeviceId = SanitizeDeviceId(deviceId);
        if (string.IsNullOrEmpty(cleanDeviceId)) return false;
        string cleanName = string.IsNullOrWhiteSpace(deviceName) ? cleanDeviceId : deviceName.Trim();

        lock (_gate)
        {
            if (!ConstantTimeEqualsUtf8(submitted, _pairingCode))
                return false;

            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string tokenHash = ComputeTokenHashHex(token);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var devices = LoadDevicesUnlocked();
            devices.RemoveAll(d => string.Equals(d.DeviceId, cleanDeviceId, StringComparison.OrdinalIgnoreCase));
            var record = new PairedDeviceRecord(cleanDeviceId, cleanName, tokenHash, now, 0);
            devices.Add(record);
            SaveDevicesUnlocked(devices);

            // Rotate pairing code after successful pairing to prevent stale code reuse
            _pairingCode = GenerateSixDigitCode();

            issuedToken = token;
            device = record;
            return true;
        }
    }

    public bool ValidateBearerToken(string? authorizationHeader, out PairedDeviceRecord? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

        string trimmed = authorizationHeader.Trim();
        if (!trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        string token = trimmed["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token)) return false;

        string candidateHash = ComputeTokenHashHex(token);
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            foreach (var d in devices)
            {
                if (ConstantTimeEqualsUtf8(candidateHash, d.TokenHashSha256))
                {
                    device = d;
                    return true;
                }
            }
        }
        return false;
    }

    public IReadOnlyList<PairedDeviceRecord> GetPairedDevices()
    {
        lock (_gate)
        {
            return LoadDevicesUnlocked();
        }
    }

    public void TouchDeviceSync(string deviceId, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            int idx = devices.FindIndex(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                devices[idx] = devices[idx] with { LastSyncMs = nowMs };
                SaveDevicesUnlocked(devices);
            }
        }
    }

    public bool UnpairDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            int removed = devices.RemoveAll(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveDevicesUnlocked(devices);
                return true;
            }
            return false;
        }
    }

    public IReadOnlyList<SyncOutboxItem> GetOutboxItems(string? deviceId = null)
    {
        lock (_gate)
        {
            return FilterOutboxForDevice(LoadOutboxUnlocked(), deviceId);
        }
    }

    public SyncOutboxItem EnqueuePlaylist(string playlistName, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("playlist", playlistName, $"歌单：{playlistName}", NormalizeMode(mode, "subscribe"), deviceId);

    public SyncOutboxItem EnqueueGenre(string genre, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("genre", genre, $"流派：{genre}", NormalizeMode(mode, "subscribe"), deviceId);

    public SyncOutboxItem EnqueueArtist(string artist, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("artist", artist, $"艺人：{artist}", NormalizeMode(mode, "subscribe"), deviceId);

    public SyncOutboxItem EnqueueTracks(
        IReadOnlyList<string> trackUids,
        string displayName,
        string deviceId = "*",
        string mode = "copy")
    {
        var distinctUids = trackUids
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string normalizedDevice = string.IsNullOrWhiteSpace(deviceId) ? "*" : deviceId.Trim();
        string resolvedMode = NormalizeMode(mode, "copy");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            var entry = new SyncOutboxItem(
                Id: "ob_" + Guid.NewGuid().ToString("N")[..12],
                DeviceId: normalizedDevice,
                Kind: "tracks",
                TargetKey: distinctUids.Count == 1 ? distinctUids[0] : $"batch:{distinctUids.Count}",
                DisplayName: string.IsNullOrWhiteSpace(displayName) ? $"自选 {distinctUids.Count} 首曲目" : displayName.Trim(),
                Mode: resolvedMode,
                TrackUids: distinctUids,
                CreatedAtMs: now
            );
            items.Add(entry);
            SaveOutboxUnlocked(items);
            return entry;
        }
    }

    public bool RemoveOutboxItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            int removed = items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveOutboxUnlocked(items);
                return true;
            }
            return false;
        }
    }

    public void ClearOutbox()
    {
        lock (_gate)
        {
            SaveOutboxUnlocked([]);
        }
    }

    public int MarkCopyItemsCompleted(string? deviceId, IEnumerable<string> completedOutboxIds)
    {
        var ids = completedOutboxIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids is null || ids.Count == 0) return 0;

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            int removed = items.RemoveAll(i =>
                ids.Contains(i.Id)
                && string.Equals(i.Mode, "copy", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(i.DeviceId, "*", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(deviceId)
                    || string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)));
            if (removed > 0)
            {
                SaveOutboxUnlocked(items);
            }
            return removed;
        }
    }

    public OutboxSummary GetOutboxSummary(string? deviceId = null)
    {
        var resolved = ResolveOutbox(deviceId);
        return new OutboxSummary(resolved.Items.Count, resolved.Tracks.Count, resolved.TotalBytes);
    }

    public ResolvedOutboxPayload ResolveOutbox(string? deviceId = null, string? hostName = null)
    {
        List<SyncOutboxItem> relevantItems;
        lock (_gate)
        {
            relevantItems = FilterOutboxForDevice(LoadOutboxUnlocked(), deviceId);
        }

        string effectiveHost = string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (relevantItems.Count == 0)
        {
            return new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, [], [], 0);
        }

        var rows = LoadManifestRows();
        var rowsByUid = new Dictionary<string, ManifestTrackRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            rowsByUid[r.TrackUid] = r;
            if (!string.IsNullOrWhiteSpace(r.AudioFrameBlake3Hex))
                rowsByUid.TryAdd(r.AudioFrameBlake3Hex, r);
        }

        var libRefs = rows.Select(r => new LocalLibraryTrackRef(
            r.TrackUid,
            r.RelPath,
            BuildFullPath(r.RelPath),
            r.Title,
            r.ArtistDisplay,
            r.Album,
            r.DurationMs,
            r.SampleRateHz
        )).ToList();

        var playlistsInDir = relevantItems.Any(i => string.Equals(i.Kind, "playlist", StringComparison.OrdinalIgnoreCase))
            ? PlaylistStore.ListPlaylists(_musicRoot, libRefs)
            : [];

        var outboxIdsByUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var resolvedTrackOrder = new List<ManifestTrackRow>();
        var resolvedPlaylists = new List<ResolvedSyncPlaylist>();
        var coverCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void AttachTrack(ManifestTrackRow row, string outboxId)
        {
            if (!TryGetValidPhysicalFileInfo(row.RelPath, out _, out long realSize, out long realMtime))
                return;

            var normalizedRow = row with { FileSize = realSize > 0 ? realSize : row.FileSize, MtimeMs = realMtime > 0 ? realMtime : row.MtimeMs };
            if (!outboxIdsByUid.TryGetValue(normalizedRow.TrackUid, out var owners))
            {
                owners = [];
                outboxIdsByUid[normalizedRow.TrackUid] = owners;
                resolvedTrackOrder.Add(normalizedRow);
            }
            if (!owners.Contains(outboxId, StringComparer.OrdinalIgnoreCase))
            {
                owners.Add(outboxId);
            }
        }

        foreach (var item in relevantItems)
        {
            switch (item.Kind.ToLowerInvariant())
            {
                case "playlist":
                {
                    var plSummary = playlistsInDir.FirstOrDefault(p =>
                        string.Equals(p.Name, item.TargetKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(p.FilePath), item.TargetKey, StringComparison.OrdinalIgnoreCase));
                    if (plSummary is null) break;

                    var doc = PlaylistStore.LoadPlaylist(plSummary.FilePath, libRefs, _musicRoot);
                    var plTrackUids = new List<string>();
                    var plRelPaths = new List<string>();
                    foreach (var entry in doc.Entries)
                    {
                        if (!entry.IsLocalMatched || string.IsNullOrWhiteSpace(entry.MatchedTrackUid))
                            continue;
                        if (!rowsByUid.TryGetValue(entry.MatchedTrackUid, out var row))
                            continue;
                        if (!TryGetValidPhysicalFileInfo(row.RelPath, out _, out _, out _))
                            continue;

                        AttachTrack(row, item.Id);
                        plTrackUids.Add(row.TrackUid);
                        plRelPaths.Add(BuildTargetRelPath(row));
                    }
                    resolvedPlaylists.Add(new ResolvedSyncPlaylist(item.Id, plSummary.Name, plRelPaths, plTrackUids));
                    break;
                }
                case "genre":
                {
                    bool matchUnlabeled = string.Equals(item.TargetKey, "(未标注流派)", StringComparison.OrdinalIgnoreCase);
                    foreach (var row in rows)
                    {
                        bool match = matchUnlabeled
                            ? string.IsNullOrWhiteSpace(row.Genre)
                            : string.Equals(row.Genre.Trim(), item.TargetKey.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (match) AttachTrack(row, item.Id);
                    }
                    break;
                }
                case "artist":
                {
                    string targetArtist = item.TargetKey.Trim();
                    foreach (var row in rows)
                    {
                        if (row.MatchesArtist(targetArtist))
                        {
                            AttachTrack(row, item.Id);
                        }
                    }
                    break;
                }
                case "tracks":
                {
                    foreach (string uid in item.TrackUids)
                    {
                        if (rowsByUid.TryGetValue(uid, out var row))
                        {
                            AttachTrack(row, item.Id);
                        }
                    }
                    break;
                }
            }
        }

        long totalBytes = 0;
        var resolvedTracks = new List<ResolvedSyncTrack>(resolvedTrackOrder.Count);
        foreach (var row in resolvedTrackOrder)
        {
            string fullPath = BuildFullPath(row.RelPath);
            string dir = Path.GetDirectoryName(fullPath) ?? "";
            bool hasCover = false;
            if (!string.IsNullOrEmpty(dir))
            {
                if (!coverCache.TryGetValue(dir, out hasCover))
                {
                    hasCover = FindCoverFileInDir(dir) is not null;
                    coverCache[dir] = hasCover;
                }
            }

            string targetRel = BuildTargetRelPath(row);
            totalBytes += row.FileSize;
            resolvedTracks.Add(new ResolvedSyncTrack(
                TrackUid: row.TrackUid,
                RelPath: row.RelPath,
                TargetRelPath: targetRel,
                Title: row.Title,
                Artist: row.ArtistDisplay,
                Album: row.Album,
                AlbumArtist: row.AlbumArtist,
                Genre: row.Genre,
                DurationMs: row.DurationMs,
                FileSize: row.FileSize,
                MtimeMs: row.MtimeMs,
                HasCover: hasCover,
                SourceOutboxIds: outboxIdsByUid[row.TrackUid]
            ));
        }

        return new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, resolvedTracks, resolvedPlaylists, totalBytes);
    }

    public IReadOnlyList<CatalogEntry> GetCatalog(string kind)
    {
        var rows = LoadManifestRows();
        switch (kind.Trim().ToLowerInvariant())
        {
            case "playlists":
            {
                var libRefs = rows.Select(r => new LocalLibraryTrackRef(
                    r.TrackUid, r.RelPath, BuildFullPath(r.RelPath),
                    r.Title, r.ArtistDisplay, r.Album, r.DurationMs, r.SampleRateHz
                )).ToList();
                var byUid = rows.ToDictionary(r => r.TrackUid, StringComparer.OrdinalIgnoreCase);
                var result = new List<CatalogEntry>();
                foreach (var pl in PlaylistStore.ListPlaylists(_musicRoot, libRefs))
                {
                    var doc = PlaylistStore.LoadPlaylist(pl.FilePath, libRefs, _musicRoot);
                    long bytes = doc.Entries
                        .Where(e => e.IsLocalMatched && e.MatchedTrackUid is not null && byUid.ContainsKey(e.MatchedTrackUid))
                        .Sum(e => byUid[e.MatchedTrackUid!].FileSize);
                    result.Add(new CatalogEntry("playlist", pl.Name, pl.Name, pl.LocalMatchedCount, bytes));
                }
                return result;
            }
            case "genres":
            {
                return rows
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.Genre) ? "(未标注流派)" : r.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new CatalogEntry("genre", g.Key, g.Key, g.Count(), g.Sum(r => r.FileSize)))
                    .OrderByDescending(c => c.TrackCount)
                    .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            case "artists":
            {
                var counts = new Dictionary<string, (string Name, int Tracks, long Bytes)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    foreach (string name in r.AllArtists)
                    {
                        if (!counts.TryGetValue(name, out var prev))
                            counts[name] = (name, 1, r.FileSize);
                        else
                            counts[name] = (prev.Name, prev.Tracks + 1, prev.Bytes + r.FileSize);
                    }
                }
                return counts.Values
                    .OrderByDescending(v => v.Tracks)
                    .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(500)
                    .Select(v => new CatalogEntry("artist", v.Name, v.Name, v.Tracks, v.Bytes))
                    .ToList();
            }
            default:
                return [];
        }
    }

    public bool TryResolveTrackPhysicalPath(string trackUid, out string fullPath, out string relPath)
    {
        fullPath = "";
        relPath = "";
        if (string.IsNullOrWhiteSpace(trackUid)) return false;

        string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
        if (!File.Exists(dbPath)) return false;

        string? dbRelPath = null;
        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT rel_path
                FROM tracks
                WHERE track_uid = $uid OR lower(hex(audio_frame_blake3)) = lower($uid)
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$uid", trackUid.Trim());
            object? res = cmd.ExecuteScalar();
            dbRelPath = res as string;
        }

        if (string.IsNullOrWhiteSpace(dbRelPath)) return false;
        if (!TryGetValidPhysicalFileInfo(dbRelPath, out string audioFull, out _, out _))
            return false;

        relPath = dbRelPath;
        fullPath = audioFull;
        return true;
    }

    public bool TryResolveTrackCoverPath(string trackUid, out string coverPath)
    {
        coverPath = "";
        if (!TryResolveTrackPhysicalPath(trackUid, out string audioFull, out _))
            return false;

        string? dir = Path.GetDirectoryName(audioFull);
        if (string.IsNullOrWhiteSpace(dir)) return false;

        string? candidate = FindCoverFileInDir(dir);
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        string rootFull = GetNormalizedRootPrefix();
        string fullCandidate = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(candidate));
        if (!fullCandidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return false;

        coverPath = fullCandidate;
        return true;
    }

    public static IReadOnlyList<string> GetLocalLanUrls(int port)
    {
        if (port <= 0) return [];
        var scored = new List<(int Score, string Url)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                int ifacePenalty = (desc.Contains("vethernet") || desc.Contains("vmware") || desc.Contains("virtualbox")
                                    || desc.Contains("wsl") || desc.Contains("tailscale") || desc.Contains("zerotier")
                                    || desc.Contains("hyper-v"))
                    ? 100
                    : (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 20);

                var ipProps = ni.GetIPProperties();
                foreach (var uni in ipProps.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = uni.Address.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;

                    int netRank = (b[0], b[1]) switch
                    {
                        (192, 168) => 1,
                        (10, _) => 2,
                        (172, >= 16 and <= 31) => 3,
                        _ => 10
                    };
                    scored.Add((ifacePenalty + netRank, $"http://{uni.Address}:{port}"));
                }
            }
        }
        catch
        {
            // Ignore interface enumeration failures
        }

        return scored
            .OrderBy(s => s.Score)
            .Select(s => s.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private SyncOutboxItem UpsertEntityOutboxItem(
        string kind,
        string targetKey,
        string displayName,
        string mode,
        string deviceId)
    {
        string cleanKey = string.IsNullOrWhiteSpace(targetKey) ? "" : targetKey.Trim();
        string cleanDevice = string.IsNullOrWhiteSpace(deviceId) ? "*" : deviceId.Trim();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            int existingIdx = items.FindIndex(i =>
                string.Equals(i.DeviceId, cleanDevice, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.TargetKey, cleanKey, StringComparison.OrdinalIgnoreCase));

            var updated = new SyncOutboxItem(
                Id: existingIdx >= 0 ? items[existingIdx].Id : "ob_" + Guid.NewGuid().ToString("N")[..12],
                DeviceId: cleanDevice,
                Kind: kind,
                TargetKey: cleanKey,
                DisplayName: displayName.Trim(),
                Mode: mode,
                TrackUids: [],
                CreatedAtMs: now
            );

            if (existingIdx >= 0) items[existingIdx] = updated;
            else items.Add(updated);

            SaveOutboxUnlocked(items);
            return updated;
        }
    }

    private bool TryGetValidPhysicalFileInfo(string relPath, out string fullPath, out long fileSize, out long mtimeMs)
    {
        fullPath = "";
        fileSize = 0;
        mtimeMs = 0;
        if (string.IsNullOrWhiteSpace(relPath)) return false;

        try
        {
            string rootFull = GetNormalizedRootPrefix();
            string combined = BuildFullPath(relPath);
            string resolved = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(combined));
            if (!resolved.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!LongPathHelper.FileExists(resolved))
                return false;

            var fi = new FileInfo(LongPathHelper.ToExtendedIfLong(resolved));
            fileSize = fi.Length;
            mtimeMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            fullPath = resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetNormalizedRootPrefix() =>
        LongPathHelper.StripExtendedPrefix(Path.GetFullPath(_musicRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private string BuildFullPath(string relPath) =>
        Path.Combine(_musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static string? FindCoverFileInDir(string dir)
    {
        if (!LongPathHelper.DirectoryExists(dir)) return null;
        foreach (string name in CoverCandidateNames)
        {
            string candidate = Path.Combine(dir, name);
            if (LongPathHelper.FileExists(candidate)) return candidate;
        }
        return null;
    }

    private static string BuildTargetRelPath(ManifestTrackRow row)
    {
        var rawSegments = row.RelPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>(rawSegments.Length);
        foreach (string seg in rawSegments)
        {
            string s = SanitizePathSegment(seg);
            if (!string.IsNullOrEmpty(s)) cleanSegments.Add(s);
        }
        if (cleanSegments.Count > 0)
        {
            return string.Join('/', cleanSegments);
        }

        string artistSeg = SanitizePathSegment(string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.ArtistDisplay : row.AlbumArtist);
        if (string.IsNullOrEmpty(artistSeg)) artistSeg = "Unknown Artist";
        string albumSeg = SanitizePathSegment(row.Album);
        if (string.IsNullOrEmpty(albumSeg)) albumSeg = "Unknown Album";
        string ext = Path.GetExtension(row.RelPath);
        if (string.IsNullOrEmpty(ext)) ext = ".flac";
        string titleSeg = SanitizePathSegment(row.Title);
        if (string.IsNullOrEmpty(titleSeg)) titleSeg = row.TrackUid;
        return $"{artistSeg}/{albumSeg}/{titleSeg}{ext}";
    }

    private static string SanitizePathSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        if (s is "." or "..") return "";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append('_');
            else
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private List<ManifestTrackRow> LoadManifestRows()
    {
        var result = new List<ManifestTrackRow>();
        string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
        if (!File.Exists(dbPath)) return result;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT track_uid, lower(hex(audio_frame_blake3)), rel_path,
                   title, album, album_artist, artists_json, composers_json, conductors_json,
                   genre, duration_ms, sample_rate_hz, file_size, mtime_ms
            FROM tracks
            ORDER BY album, rel_path;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string blake3 = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string relPath = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string title = reader.IsDBNull(3) ? "" : reader.GetString(3);
            string album = reader.IsDBNull(4) ? "" : reader.GetString(4);
            string albumArtist = reader.IsDBNull(5) ? "" : reader.GetString(5);
            string artistsJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6);
            string composersJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
            string conductorsJson = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
            string genre = reader.IsDBNull(9) ? "" : reader.GetString(9);
            int durationMs = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            int sampleRate = reader.IsDBNull(11) ? 44100 : reader.GetInt32(11);
            long fileSize = reader.IsDBNull(12) ? 0L : reader.GetInt64(12);
            long mtimeMs = reader.IsDBNull(13) ? 0L : reader.GetInt64(13);

            result.Add(new ManifestTrackRow(
                uid, blake3, relPath, title, album, albumArtist,
                artistsJson, composersJson, conductorsJson,
                genre, durationMs, sampleRate, fileSize, mtimeMs));
        }
        return result;
    }

    private sealed record ManifestTrackRow(
        string TrackUid,
        string AudioFrameBlake3Hex,
        string RelPath,
        string Title,
        string Album,
        string AlbumArtist,
        string ArtistsJson,
        string ComposersJson,
        string ConductorsJson,
        string Genre,
        int DurationMs,
        int SampleRateHz,
        long FileSize,
        long MtimeMs
    )
    {
        private List<string>? _allArtists;

        public string ArtistDisplay =>
            ParseJsonArray(ArtistsJson) is { Count: > 0 } arr
                ? string.Join(", ", arr)
                : AlbumArtist;

        public IReadOnlyList<string> AllArtists
        {
            get
            {
                if (_allArtists is not null) return _allArtists;
                var list = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddSplit(string? raw)
                {
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    foreach (string s in ArtistEntitySplitter.SplitPerformers(raw))
                    {
                        if (seen.Add(s)) list.Add(s);
                    }
                }

                AddSplit(AlbumArtist);
                foreach (string item in ParseJsonArray(ArtistsJson)) AddSplit(item);
                foreach (string item in ParseJsonArray(ComposersJson)) AddSplit(item);
                foreach (string item in ParseJsonArray(ConductorsJson)) AddSplit(item);
                _allArtists = list;
                return _allArtists;
            }
        }

        public bool MatchesArtist(string target) =>
            AllArtists.Any(a => string.Equals(a, target, StringComparison.OrdinalIgnoreCase));

        private static List<string> ParseJsonArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    private static List<SyncOutboxItem> FilterOutboxForDevice(List<SyncOutboxItem> all, string? deviceId) =>
        all.Where(i =>
            string.Equals(i.DeviceId, "*", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(deviceId)
            || string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.CreatedAtMs)
            .ToList();

    private List<PairedDeviceRecord> LoadDevicesUnlocked()
    {
        if (!File.Exists(_devicesPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PairedDeviceRecord>>(File.ReadAllText(_devicesPath, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveDevicesUnlocked(List<PairedDeviceRecord> devices)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string json = JsonSerializer.Serialize(devices, JsonWriteOptions);
        File.WriteAllText(_devicesPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private List<SyncOutboxItem> LoadOutboxUnlocked()
    {
        if (!File.Exists(_outboxPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SyncOutboxItem>>(File.ReadAllText(_outboxPath, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveOutboxUnlocked(List<SyncOutboxItem> items)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string json = JsonSerializer.Serialize(items, JsonWriteOptions);
        File.WriteAllText(_outboxPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string NormalizeMode(string mode, string defaultMode)
    {
        string clean = mode?.Trim().ToLowerInvariant() ?? "";
        return clean is "subscribe" or "copy" ? clean : defaultMode;
    }

    private static string GenerateSixDigitCode() =>
        RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString("D6");

    private static string ComputeTokenHashHex(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool ConstantTimeEqualsUtf8(string a, string b)
    {
        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string SanitizeDeviceId(string id)
    {
        string cleaned = string.Concat(id.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
